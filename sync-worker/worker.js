/**
 * Maridew Finance - Cloud Sync Worker v3 (Cloudflare Workers + KV)
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
        return json({ ok: true, service: 'maridew-sync', v: 3, time: new Date().toISOString() }, cors);
      }

      if (url.pathname === '/signup' && request.method === 'POST') {
        const { username, authHash } = await body(request);
        const name = normName(username);
        if (!name || name.length < 3) return json({ ok: false, error: 'Username must be at least 3 characters.' }, cors, 400);
        if (!authHash || String(authHash).length < 32) return json({ ok: false, error: 'Invalid auth hash.' }, cors, 400);

        const key = 'user:' + name;
        // Brief retry: a duplicate signup seconds after the first could read
        // a stale (missing) copy of the user record.
        let existing = await env.SYNC_KV.get(key);
        for (let a = 0; a < 3 && !existing; a++) {
          await sleep(500);
          existing = await env.SYNC_KV.get(key);
        }
        if (existing) return json({ ok: false, error: 'That username is already taken.' }, cors, 409);

        const record = { authHash: await serverHash(authHash), createdAt: new Date().toISOString() };
        await env.SYNC_KV.put(key, JSON.stringify(record));
        return json({ ok: true, token: await issueToken(env, name), updatedAt: null }, cors, 201);
      }

      if (url.pathname === '/signin' && request.method === 'POST') {
        const { username, authHash } = await body(request);
        const name = normName(username);
        const key = 'user:' + name;
        let raw = await env.SYNC_KV.get(key);
        for (let a = 0; a < 3 && !raw; a++) { await sleep(500); raw = await env.SYNC_KV.get(key); }
        if (!raw) return json({ ok: false, error: 'No account with that username.' }, cors, 404);
        const record = JSON.parse(raw);
        if (record.authHash !== await serverHash(authHash)) {
          return json({ ok: false, error: 'Incorrect password.' }, cors, 401);
        }
        const [blobR, token] = await Promise.all([
          env.SYNC_KV.get('data:' + name),
          issueToken(env, name),
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
        await env.SYNC_KV.put(key, JSON.stringify(record));
        return json({ ok: true }, cors);
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

function hmacKey(env) {
  return crypto.subtle.importKey('raw', new TextEncoder().encode(env.TOKEN_SECRET), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
}
async function issueToken(env, name) {
  const exp = Math.floor(Date.now() / 1000) + TOKEN_TTL_S;
  const payload = name + '.' + exp;
  const sig = await crypto.subtle.sign('HMAC', await hmacKey(env), new TextEncoder().encode(payload));
  const sigHex = [...new Uint8Array(sig)].map(b => b.toString(16).padStart(2, '0')).join('');
  return payload + '.' + sigHex;
}
async function authName(env, request) {
  const header = request.headers.get('Authorization') || '';
  const token = header.startsWith('Bearer ') ? header.slice(7).trim() : '';
  if (!env.TOKEN_SECRET || !token) return null;
  const parts = token.split('.');
  if (parts.length !== 3) return null;
  const [name, expStr, sigHex] = parts;
  const exp = parseInt(expStr, 10);
  if (!name || !exp || exp < Math.floor(Date.now() / 1000)) return null;
  const sig = await crypto.subtle.sign('HMAC', await hmacKey(env), new TextEncoder().encode(name + '.' + expStr));
  const expect = [...new Uint8Array(sig)].map(b => b.toString(16).padStart(2, '0')).join('');
  if (sigHex.length !== expect.length) return null;
  let diff = 0;
  for (let i = 0; i < expect.length; i++) diff |= sigHex.charCodeAt(i) ^ expect.charCodeAt(i);
  return diff === 0 ? name : null;
}
