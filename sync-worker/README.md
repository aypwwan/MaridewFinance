# Maridew Sync Worker

Zero-knowledge cloud sync backend for the Maridew Finance web edition,
running on Cloudflare Workers (free tier) + Workers KV.

## Why zero-knowledge

The browser derives two keys from the user's password (PBKDF2-SHA256, 150k
rounds):

- `authKey` (hex) — sent to the server as the login proof
- `encKey` (AES-256) — **never leaves the browser**; all data is AES-GCM
  encrypted with it before upload

The server stores only: `SHA-256(serverSalt | authKey)` plus the ciphertext
blob. A database leak reveals nothing usable: no passwords, no plaintext
data, and even the stored hash cannot be replayed (the server re-hashes it).

## Endpoints

| Route | Auth | Purpose |
|---|---|---|
| `GET /health` | — | liveness |
| `POST /signup` `{username, authHash}` | — | create account, returns bearer token |
| `POST /signin` `{username, authHash}` | — | verify, returns token + latest blob |
| `GET /data` | Bearer | fetch encrypted blob |
| `PUT /data` `{blob}` | Bearer | store encrypted blob (4 MB cap) |

## Deploy

```bash
cd sync-worker
npx wrangler kv namespace create SYNC_KV   # copy the id into wrangler.toml
npx wrangler deploy
```

## Client wiring

`wwwroot/web-bridge.js` reads the sync URL from `SYNC_SERVER` (top of file).
Empty = local-only accounts (IndexedDB). Set = cloud accounts with local
cache; data pushes are debounced after each save, pulls happen at sign-in.
