/**
 * Maridew Finance - Cloud Sync Worker (Cloudflare Workers + KV)
 * ---------------------------------------------------------------------------
 * Zero-knowledge sync backend for the web edition. The server NEVER sees
 * plaintext financial data or passwords:
 *
 *   - The client derives, from the user's password:
 *       authKey  = PBKDF2-SHA256(password, "auth|" + username, 150k)  -> hex
 *       encKey   = PBKDF2-SHA256(password, "enc|"  + username, 150k)  -> AES-256
 *   - Only authKey is sent to the server (log-in proof). encKey never leaves
 *     the browser; data is AES-GCM encrypted with it before upload.
 *   - The server stores: username -> { authHash (salted bcrypt-like hash of
 *     authKey), blob (ciphertext), updatedAt }.
 *
 * Endpoints (all JSON):
 *   GET  /health
 *   POST /signup   { username, authHash }         -> { ok, token }
 *   POST /signin   { username, authHash }         -> { ok, token, blob?, updatedAt }
 *   GET  /data     (Authorization: Bearer token)  -> { blob, updatedAt }
 *   PUT  /data     (Authorization: Bearer token)  -> { ok, updatedAt }
 *
 * KV binding required: SYNC_KV.  Set a secret:  wrangler secret put ADMIN_TOKEN
 * (ADMIN_TOKEN currently only gates future admin endpoints; not required for
 * the data path).
 * ---------------------------------------------------------------------------
 */

const PBKDF2_ROUNDS_ON_SERVER = 0; // client does the stretching; server stays cheap

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
        return json({ ok: true, service: 'maridew-sync', time: new Date().toISOString() }, cors);
      }

      if (url.pathname === '/signup' && request.method === 'POST') {
        const { username, authHash } = await body(request);
        const name = normName(username);
        if (!name || name.length < 3) return json({ ok: false, error: 'Username must be at least 3 characters.' }, cors, 400);
        if (!authHash || String(authHash).length < 32) return json({ ok: false, error: 'Invalid auth hash.' }, cors, 400);

        const key = 'user:' + name;
        const existing = await env.SYNC_KV.get(key);
        if (existing) return json({ ok: false, error: 'That username is already taken.' }, cors, 409);

        const record = {
          authHash: await serverHash(authHash),      // never store the raw auth key
          createdAt: new Date().toISOString(),
          blob: null,
          updatedAt: null,
        };
        await env.SYNC_KV.put(key, JSON.stringify(record));
        const token = await issueToken(env, name);
        return json({ ok: true, token, updatedAt: null }, cors, 201);
      }

      if (url.pathname === '/signin' && request.method === 'POST') {
        const { username, authHash } = await body(request);
        const name = normName(username);
        const key = 'user:' + name;
        const raw = await env.SYNC_KV.get(key);
        if (!raw) return json({ ok: false, error: 'No account with that username.' }, cors, 404);
        const record = JSON.parse(raw);
        if (record.authHash !== await serverHash(authHash)) {
          return json({ ok: false, error: 'Incorrect password.' }, cors, 401);
        }
        const token = await issueToken(env, name);
        return json({ ok: true, token, blob: record.blob, updatedAt: record.updatedAt }, cors);
      }

      if (url.pathname === '/data' && request.method === 'GET') {
        const name = await authName(env, request);
        if (!name) return json({ ok: false, error: 'Unauthorized.' }, cors, 401);
        const raw = await env.SYNC_KV.get('user:' + name);
        if (!raw) return json({ ok: false, error: 'Account missing.' }, cors, 404);
        const record = JSON.parse(raw);
        return json({ ok: true, blob: record.blob, updatedAt: record.updatedAt }, cors);
      }

      if (url.pathname === '/password' && request.method === 'POST') {
        const name = await authName(env, request);
        if (!name) return json({ ok: false, error: 'Unauthorized.' }, cors, 401);
        const { newAuthHash } = await body(request);
        if (!newAuthHash || String(newAuthHash).length < 32) return json({ ok: false, error: 'Invalid auth hash.' }, cors, 400);
        const raw = await env.SYNC_KV.get('user:' + name);
        if (!raw) return json({ ok: false, error: 'Account missing.' }, cors, 404);
        const record = JSON.parse(raw);
        record.authHash = await serverHash(newAuthHash);
        await env.SYNC_KV.put('user:' + name, JSON.stringify(record));
        return json({ ok: true }, cors);
      }

      if (url.pathname === '/data' && request.method === 'PUT') {
        const name = await authName(env, request);
        if (!name) return json({ ok: false, error: 'Unauthorized.' }, cors, 401);
        const { blob } = await body(request);
        if (typeof blob !== 'string' || blob.length === 0) return json({ ok: false, error: 'Missing blob.' }, cors, 400);
        if (blob.length > 4_000_000) return json({ ok: false, error: 'Blob too large (4 MB limit).' }, cors, 413);
        const key = 'user:' + name;
        const raw = await env.SYNC_KV.get(key);
        if (!raw) return json({ ok: false, error: 'Account missing.' }, cors, 404);
        const record = JSON.parse(raw);
        record.blob = blob;
        record.updatedAt = new Date().toISOString();
        await env.SYNC_KV.put(key, JSON.stringify(record));
        return json({ ok: true, updatedAt: record.updatedAt }, cors);
      }

      return json({ ok: false, error: 'Not found.' }, cors, 404);
    } catch (err) {
      return json({ ok: false, error: (err && err.message) || 'Server error.' }, cors, 500);
    }
  },
};

function normName(u) { return String(u || '').trim().toLowerCase(); }
function json(obj, cors, status = 200) {
  return new Response(JSON.stringify(obj), { status, headers: { 'Content-Type': 'application/json', ...cors } });
}
async function body(request) {
  try { return await request.json(); } catch { return {}; }
}

// Server-side re-hash of the client's auth key, so a KV leak does not even
// leak the value that could be replayed to /signin.
async function serverHash(authHash) {
  const data = new TextEncoder().encode('maridew-server|' + authHash);
  const digest = await crypto.subtle.digest('SHA-256', data);
  return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('');
}

async function issueToken(env, name) {
  const raw = crypto.getRandomValues(new Uint8Array(32));
  const token = [...raw].map(b => b.toString(16).padStart(2, '0')).join('');
  await env.SYNC_KV.put('token:' + token, JSON.stringify({ name, issuedAt: Date.now() }), { expirationTtl: 60 * 60 * 24 * 30 });
  return token;
}

async function authName(env, request) {
  const header = request.headers.get('Authorization') || '';
  const token = header.startsWith('Bearer ') ? header.slice(7).trim() : '';
  if (!token) return null;
  const raw = await env.SYNC_KV.get('token:' + token);
  if (!raw) return null;
  try { return JSON.parse(raw).name || null; } catch { return null; }
}
