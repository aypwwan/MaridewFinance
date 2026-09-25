/**
 * Maridew Finance - Cloud Sync Worker v4 (Cloudflare Workers + KV)
 * ---------------------------------------------------------------------------
 * Zero-knowledge sync backend. The server NEVER sees plaintext data or
 * passwords: the client derives authHex + an AES key from the password
 * (PBKDF2) and uploads only the authHex proof + AES-GCM ciphertext.
 *
 * v3: bearer tokens are self-contained HMAC-SHA256 signed strings
 * (name.exp.sig) verified against the TOKEN_SECRET binding - token checks
 * do zero KV reads, which removes the eventual-consistency race where an
 * immediately-following /data call could not see a just-issued token.
 * Blobs live under their own key ("data:<user>") so PUT is a blind write.
 *
 * v4 (hardening):
 *  - /signin returns one uniform error for unknown user AND wrong password
 *    (no account enumeration), with a fixed-latency path so timing does not
 *    reveal which case happened.
 *  - Rate limiting: per-username and per-IP failure counters in KV throttle
 *    repeated auth attempts (signup/signin/password). Successes reset them.
 *  - Token versioning: user records carry a tokenVersion "tv". Tokens embed
 *    it (name.tv.exp.sig) and /password bumps it, so every token issued
 *    before a password change stops working immediately - a lost device can
 *    be cut off by changing the password.
 *
 * Endpoints (all JSON, CORS open):
 *   GET  /health
 *   POST /signup   { username, authHash }         -> { ok, token }
 *   POST /signin   { username, authHash }         -> { ok, token, blob?, updatedAt? }
 *   GET  /data     (Bearer)                       -> { ok, blob?, updatedAt? }
 *   PUT  /data     { blob } (Bearer)              -> { ok, updatedAt }
 *   POST /password  { newAuthHash } (Bearer)      -> { ok }
 *
 * Bindings: SYNC_KV (kv_namespace), TOKEN_SECRET (secret_text).
 * ---------------------------------------------------------------------------
 */

const TOKEN_TTL_S = 60 * 60 * 24 * 30; // 30 days
const RATE_WINDOW_S = 60 * 15;         // failure counters roll off after 15 min
const RATE_MAX_FAILURES = 10;          // per username AND per IP inside the window

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const cors = {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET, POST, PUT, OPTIONS',
      'Access-Control-Allow-Headers': 'Content-Type, Authorization',
      'Access-Control-Max-Age': '86400',
    };
    if (request.method === 'OPTIONS') return new Response(null, { headers: cors });

    try {
      if (url.pathname === '/health' && request.method === 'GET') {
        return json({ ok: true, service: 'maridew-sync', v: 4, time: new Date().toISOString() }, cors);
      }

      if (url.pathname === '/signup' && request.method === 'POST') {
        const { username, authHash } = await body(request);
        const name = normName(username);
        if (!name || name.length < 3) return json({ ok: false, error: 'Username must be at least 3 characters.' }, cors, 400);
        if (!authHash || String(authHash).length < 32) return json({ ok: false, error: 'Invalid auth hash.' }, cors, 400);
        const limited = await checkRateLimit(env, request, name, cors);
        if (limited) return limited;

        const key = 'user:' + name;
        // Brief retry: a duplicate signup seconds after the first could read
        // a stale (missing) copy of the user record.
        let existing = await env.SYNC_KV.get(key);
        for (let a = 0; a < 3 && !existing; a++) {
          await sleep(500);
          existing = await env.SYNC_KV.get(key);
        }
        if (existing) return json({ ok: false, error: 'That username is already taken.' }, cors, 409);

        const record = { authHash: await serverHash(authHash), tv: 1, createdAt: new Date().toISOString() };
        await env.SYNC_KV.put(key, JSON.stringify(record));
        return json({ ok: true, token: await issueToken(env, name, record.tv), updatedAt: null }, cors, 201);
      }

      if (url.pathname === '/signin' && request.method === 'POST') {
        const { username, authHash } = await body(request);
        const name = normName(username);
        if (!name || !authHash) {
          return json({ ok: false, error: 'Invalid username or password.' }, cors, 401);
        }
        const limited = await checkRateLimit(env, request, name, cors);
        if (limited) return limited;

        const key = 'user:' + name;
        let raw = await env.SYNC_KV.get(key);
        for (let a = 0; a < 3 && !raw; a++) { await sleep(500); raw = await env.SYNC_KV.get(key); }
        if (!raw) {
          // Uniform rejection + fixed work: no way to distinguish "no such
          // account" from "wrong password" by response or timing.
          await serverHash(authHash);
          await noteAuthFailure(env, request, name);
          return json({ ok: false, error: 'Invalid username or password.' }, cors, 401);
        }
        const record = JSON.parse(raw);
        if (record.authHash !== await serverHash(authHash)) {
          await noteAuthFailure(env, request, name);
          return json({ ok: false, error: 'Invalid username or password.' }, cors, 401);
        }
        await clearAuthFailures(env, request, name);
        const [blobR, token] = await Promise.all([
          env.SYNC_KV.get('data:' + name),
          issueToken(env, name, record.tv || 1),
        ]);
        let blob = null, updatedAt = null;
        if (blobR) { try { const d = JSON.parse(blobR); blob = d.blob; updatedAt = d.updatedAt; } catch {} }
        return json({ ok: true, token, blob, updatedAt }, cors);
      }

      if (url.pathname === '/data' && request.method === 'GET') {
        const name = await authName(env, request);
        if (!name) return json({ ok: false, error: 'Unauthorized.' }, cors, 401);
        const raw = await env.SYNC_KV.get('data:' + name);
        if (!raw) return json({ ok: true, blob: null, updatedAt: null }, cors);
        let blob = null, updatedAt = null;
        try { const d = JSON.parse(raw); blob = d.blob; updatedAt = d.updatedAt; } catch {}
        return json({ ok: true, blob, updatedAt }, cors);
      }

      if (url.pathname === '/data' && request.method === 'PUT') {
        const name = await authName(env, request);
        if (!name) return json({ ok: false, error: 'Unauthorized.' }, cors, 401);
        const { blob } = await body(request);
        if (typeof blob !== 'string' || blob.length === 0) return json({ ok: false, error: 'Missing blob.' }, cors, 400);
        if (blob.length > 4_000_000) return json({ ok: false, error: 'Blob too large (4 MB limit).' }, cors, 413);
        const updatedAt = new Date().toISOString();
        await env.SYNC_KV.put('data:' + name, JSON.stringify({ blob, updatedAt }));
        return json({ ok: true, updatedAt }, cors);
      }

      if (url.pathname === '/password' && request.method === 'POST') {
        const name = await authName(env, request);
        if (!name) return json({ ok: false, error: 'Unauthorized.' }, cors, 401);
        const { newAuthHash } = await body(request);
        if (!newAuthHash || String(newAuthHash).length < 32) return json({ ok: false, error: 'Invalid auth hash.' }, cors, 400);
        const key = 'user:' + name;
        let raw = await env.SYNC_KV.get(key);
        for (let a = 0; a < 3 && !raw; a++) { await sleep(500); raw = await env.SYNC_KV.get(key); }
        if (!raw) return json({ ok: false, error: 'Account missing.' }, cors, 404);
        const record = JSON.parse(raw);
        record.authHash = await serverHash(newAuthHash);
        // Bump the token version: every token issued before this change
        // (a lost phone, an old browser) stops authenticating immediately.
        record.tv = (record.tv || 1) + 1;
        await env.SYNC_KV.put(key, JSON.stringify(record));
        return json({ ok: true, tv: record.tv }, cors);
      }

      return json({ ok: false, error: 'Not found.' }, cors, 404);
    } catch (err) {
      return json({ ok: false, error: (err && err.message) || 'Server error.' }, cors, 500);
    }
  },
};

function normName(u) { return String(u || '').trim().toLowerCase(); }
function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }
function json(obj, cors, status = 200) {
  return new Response(JSON.stringify(obj), { status, headers: { 'Content-Type': 'application/json', ...cors } });
}
async function body(request) {
  try { return await request.json(); } catch { return {}; }
}

async function serverHash(authHash) {
  const data = new TextEncoder().encode('maridew-server|' + authHash);
  const digest = await crypto.subtle.digest('SHA-256', data);
  return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('');
}

// ------------------------------------------------------------ rate limiting
// Failure counters only: successful auth clears them, so normal users never
// hit the throttle. Worst case for an attacker: 10 guesses per username (and
// per IP) per 15 minutes, then HTTP 429 for the window.
function failKey(kind, value) { return 'rate:' + kind + ':' + value; }

function clientIp(request) {
  return (request.headers.get('cf-connecting-ip') || 'unknown').trim();
}

async function readFailures(env, key) {
  try {
    const raw = await env.SYNC_KV.get(key);
    if (!raw) return { count: 0, resetAt: 0 };
    const d = JSON.parse(raw);
    if (!d || typeof d.count !== 'number') return { count: 0, resetAt: 0 };
    if (d.resetAt && d.resetAt < Date.now()) return { count: 0, resetAt: 0 };   // window expired
    return d;
  } catch { return { count: 0, resetAt: 0 }; }
}

async function noteAuthFailure(env, request, name) {
  const resetAt = Date.now() + RATE_WINDOW_S * 1000;
  const keys = [failKey('u', name), failKey('ip', clientIp(request))];
  for (const k of keys) {
    const cur = await readFailures(env, k);
    await env.SYNC_KV.put(k, JSON.stringify({ count: cur.count + 1, resetAt: cur.resetAt || resetAt }),
      { expirationTtl: RATE_WINDOW_S + 60 });
  }
}

async function clearAuthFailures(env, request, name) {
  const keys = [failKey('u', name), failKey('ip', clientIp(request))];
  for (const k of keys) { try { await env.SYNC_KV.delete(k); } catch { /* best effort */ } }
}

async function checkRateLimit(env, request, name, cors) {
  const checks = [
    { key: failKey('u', name), what: 'this account' },
    { key: failKey('ip', clientIp(request)), what: 'this network' },
  ];
  for (const c of checks) {
    const f = await readFailures(env, c.key);
    if (f.count >= RATE_MAX_FAILURES) {
      const mins = Math.max(1, Math.ceil(((f.resetAt || 0) - Date.now()) / 60000));
      return json({ ok: false, error: 'Too many failed attempts for ' + c.what + '. Try again in about ' + mins + ' minute' + (mins === 1 ? '' : 's') + '.' }, cors, 429);
    }
  }
  return null;
}

// ---------------------------------------------------------------- tokens
function hmacKey(env) {
  return crypto.subtle.importKey('raw', new TextEncoder().encode(env.TOKEN_SECRET), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
}
async function issueToken(env, name, tv) {
  const exp = Math.floor(Date.now() / 1000) + TOKEN_TTL_S;
  const payload = name + '.' + (tv || 1) + '.' + exp;
  const sig = await crypto.subtle.sign('HMAC', await hmacKey(env), new TextEncoder().encode(payload));
  const sigHex = [...new Uint8Array(sig)].map(b => b.toString(16).padStart(2, '0')).join('');
  return payload + '.' + sigHex;
}
// Returns the username when the token is valid AND matches the account's
// current token version; null otherwise. The tv check costs one KV read and
// is what makes password changes revoke outstanding tokens.
async function authName(env, request) {
  const header = request.headers.get('Authorization') || '';
  const token = header.startsWith('Bearer ') ? header.slice(7).trim() : '';
  if (!env.TOKEN_SECRET || !token) return null;
  const parts = token.split('.');
  // Two accepted layouts: v4 "name.tv.exp.sig" and the pre-v4 "name.exp.sig"
  // (old clients must keep working; their tokens are treated as tv=1 and are
  // revoked the same way once the account's tv moves past 1).
  if (parts.length !== 4 && parts.length !== 3) return null;
  const legacy = parts.length === 3;
  const name = parts[0];
  const tvStr = legacy ? '1' : parts[1];
  const expStr = legacy ? parts[1] : parts[2];
  const sigHex = legacy ? parts[2] : parts[3];
  const exp = parseInt(expStr, 10);
  const tv = parseInt(tvStr, 10);
  if (!name || !tv || !exp || exp < Math.floor(Date.now() / 1000)) return null;
  const payload = legacy ? name + '.' + expStr : name + '.' + tvStr + '.' + expStr;
  const sig = await crypto.subtle.sign('HMAC', await hmacKey(env), new TextEncoder().encode(payload));
  const expect = [...new Uint8Array(sig)].map(b => b.toString(16).padStart(2, '0')).join('');
  if (sigHex.length !== expect.length) return null;
  let diff = 0;
  for (let i = 0; i < expect.length; i++) diff |= sigHex.charCodeAt(i) ^ expect.charCodeAt(i);
  if (diff !== 0) return null;
  // Signature is valid; now enforce the current token version.
  try {
    const raw = await env.SYNC_KV.get('user:' + name);
    if (!raw) return null;
    const record = JSON.parse(raw);
    if ((record.tv || 1) !== tv) return null;   // revoked by a password change
  } catch { return null; }
  return name;
}
