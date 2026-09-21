/*!
 * Maridew Finance - Web Bridge
 * ---------------------------------------------------------------------------
 * Lets the SAME dashboard (wwwroot/index.html) run in a plain browser with no
 * desktop host. When the page runs inside WebView2 the real C# bridges exist
 * (window.chrome.webview.hostObjects.*) and this file does nothing at all.
 *
 * In a browser it installs:
 *   - window.maridewDbBridge     (mirrors DbBridge.cs surface)
 *   - window.maridewUpdateBridge (mirrors UpdateService.cs surface)
 *   plus sign-in / create-account and lock-screen overlays so the page's own
 *   auth and auto-lock flows keep working unchanged.
 *
 * Storage: IndexedDB ("maridew-web"), per-user, on-device only. Data never
 * leaves the browser; users can back it up with Export (JSON download) in the
 * Settings tab.
 * ---------------------------------------------------------------------------
 */
(function () {
    'use strict';

    // ---- Activation guard: real desktop host present? Then do nothing. ----
    var host = (window.chrome && window.chrome.webview && window.chrome.webview.hostObjects) || null;
    if (host && (host.dbBridge || host.updateBridge)) { return; }

    // Keep in sync with UpdateService.CurrentVersion (v-bump: release day).
    var WEB_VERSION = '1.0.3';

    // ---- Cloud sync (zero-knowledge). Empty = local-only accounts. ----
    // When set, accounts live on the sync Worker and data is AES-GCM
    // encrypted in THIS browser (PBKDF2-derived key) before upload; the
    // server stores only opaque ciphertext + a login-proof hash.
    var SYNC_SERVER = 'https://maridew-sync.maridew.workers.dev';

    var DB_NAME = 'maridew-web';
    var DB_VERSION = 1;
    var STORE_USERS = 'users';
    var STORE_KV = 'kv';        // settings + session
    var STORE_DATA = 'data';    // per-user table rows

    var TABLES = ['transactions', 'budgets', 'loans', 'savings_goals',
                  'categories', 'savings_transactions', 'loan_transactions', 'accounts'];

    var _dbPromise = null;
    var _currentUserId = 0;

    // ------------------------------------------------------------------ idb
    function openDb() {
        if (_dbPromise) return _dbPromise;
        _dbPromise = new Promise(function (resolve, reject) {
            var req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = function () {
                var d = req.result;
                if (!d.objectStoreNames.contains(STORE_USERS)) {
                    var users = d.createObjectStore(STORE_USERS, { keyPath: 'id', autoIncrement: true });
                    users.createIndex('usernameLower', 'usernameLower', { unique: true });
                }
                if (!d.objectStoreNames.contains(STORE_KV)) d.createObjectStore(STORE_KV, { keyPath: 'k' });
                if (!d.objectStoreNames.contains(STORE_DATA)) d.createObjectStore(STORE_DATA, { keyPath: 'k' });
            };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror = function () { reject(req.error); };
        });
        return _dbPromise;
    }

    function tx(store, mode, fn) {
        return openDb().then(function (d) {
            return new Promise(function (resolve, reject) {
                var t = d.transaction(store, mode);
                var s = t.objectStore(store);
                var out;
                try { out = fn(s); } catch (e) { reject(e); return; }
                t.oncomplete = function () { resolve(out && out._req ? out._req.result : out); };
                t.onerror = function () { reject(t.error); };
                t.onabort = function () { reject(t.error); };
            });
        });
    }
    function req(r) { return { _req: r }; }

    function dbGet(store, key) { return tx(store, 'readonly', function (s) { return req(s.get(key)); }); }
    function dbPut(store, value) { return tx(store, 'readwrite', function (s) { return req(s.put(value)); }); }
    function dbDelete(store, key) { return tx(store, 'readwrite', function (s) { return req(s['delete'](key)); }); }

    function dataKey(uid, table) { return uid + ':' + table; }
    function settingsKey(uid) { return uid + ':settings'; }

    function requireUser() {
        if (!_currentUserId) throw new Error('No user is signed in.');
        return _currentUserId;
    }

    // Every entry point that needs the session awaits its restore first.
    function withUser(fn) {
        return sessionReady().then(requireUser).then(fn);
    }

    function fail(msg) { return JSON.stringify({ ok: false, error: msg }); }

    // --------------------------------------------------------------- crypto
    function bytesToHex(b) {
        var s = '';
        for (var i = 0; i < b.length; i++) s += (b[i] >>> 4).toString(16) + (b[i] & 15).toString(16);
        return s;
    }
    function hexToBytes(h) {
        var out = new Uint8Array(h.length / 2);
        for (var i = 0; i < out.length; i++) out[i] = parseInt(h.substr(i * 2, 2), 16);
        return out;
    }
    function concatBytes(a, b) {
        var out = new Uint8Array(a.length + b.length);
        out.set(a, 0); out.set(b, a.length);
        return out;
    }

    // SHA-256 stretch (8 rounds). Browser edition only; desktop uses
    // Rfc2899/PBKDF2 in AuthService.cs. Requires a secure context (https),
    // which any real host (GitHub Pages) provides; falls back on file://.
    function hashPassword(password, saltHex) {
        var salt = hexToBytes(saltHex);
        var enc = new TextEncoder();
        if (!(window.crypto && window.crypto.subtle)) {
            // file:// fallback: still salted, but not cryptographic. The
            // browser warns loudly enough about local files; data stays local.
            var h = 5381;
            var s = password + '|' + saltHex;
            for (var i = 0; i < s.length; i++) { h = ((h << 5) + h + s.charCodeAt(i)) | 0; }
            return Promise.resolve('fb$' + (h >>> 0).toString(16) + '$' + s.length);
        }
        var p = enc.encode(password);
        function round(input) {
            return window.crypto.subtle.digest('SHA-256', concatBytes(input, salt))
                .then(function (buf) { return new Uint8Array(buf); });
        }
        var chain = round(p);
        for (var r = 1; r < 8; r++) {
            (function () { var prev = chain; chain = prev.then(round); })();
        }
        return chain.then(function (finalBytes) { return bytesToHex(finalBytes); });
    }

    function newSalt() {
        var b = new Uint8Array(16);
        if (window.crypto && window.crypto.getRandomValues) {
            window.crypto.getRandomValues(b);
        } else {
            for (var i = 0; i < b.length; i++) b[i] = Math.floor(Math.random() * 256);
        }
        return bytesToHex(b);
    }

    // ------------------------------------------------- cloud sync helpers
    // Two keys from one password: authHex proves identity to the server;
    // encKey never leaves this browser and encrypts every uploaded byte.
    function deriveCloudKeys(username, password) {
        var enc = new TextEncoder();
        var name = String(username || '').trim().toLowerCase();
        return crypto.subtle.importKey('raw', enc.encode(password), 'PBKDF2', false, ['deriveBits', 'deriveKey'])
            .then(function (baseKey) {
                return Promise.all([
                    crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt: enc.encode('auth|' + name), iterations: 150000 }, baseKey, 256),
                    crypto.subtle.deriveKey({ name: 'PBKDF2', hash: 'SHA-256', salt: enc.encode('enc|' + name), iterations: 150000 }, baseKey, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt'])
                ]);
            }).then(function (res) {
                return { authHex: bytesToHex(new Uint8Array(res[0])), encKey: res[1] };
            });
    }

    function b64(bytes) {
        var s = '';
        new Uint8Array(bytes).forEach(function (b) { s += String.fromCharCode(b); });
        return btoa(s);
    }
    function unb64(str) {
        var s = atob(str), out = new Uint8Array(s.length);
        for (var i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
        return out;
    }
    function encryptPayload(encKey, obj) {
        var iv = crypto.getRandomValues(new Uint8Array(12));
        var data = new TextEncoder().encode(JSON.stringify(obj));
        return crypto.subtle.encrypt({ name: 'AES-GCM', iv: iv }, encKey, data)
            .then(function (ct) { return b64(iv) + ':' + b64(ct); });
    }
    function decryptPayload(encKey, blob) {
        var parts = String(blob).split(':');
        if (parts.length !== 2) return Promise.reject(new Error('Bad blob.'));
        return crypto.subtle.decrypt({ name: 'AES-GCM', iv: unb64(parts[0]) }, encKey, unb64(parts[1]))
            .then(function (pt) { return JSON.parse(new TextDecoder().decode(pt)); });
    }

    function cloudApi(path, opts) {
        return fetch(SYNC_SERVER + path, Object.assign({ headers: { 'Content-Type': 'application/json' } }, opts || {}))
            .then(function (r) { return r.json()['catch'](function () { return { ok: false, error: 'Bad response from sync server.' }; }); });
    }
    function getCloudToken(uid) { return dbGet(STORE_KV, 'cloudtoken:' + uid).then(function (row) { return (row && row.token) || null; }); }
    function saveCloudToken(uid, token) { return token ? dbPut(STORE_KV, { k: 'cloudtoken:' + uid, token: token }) : dbDelete(STORE_KV, 'cloudtoken:' + uid); }
    function getCloudEncKey(uid) { return dbGet(STORE_KV, 'cloudenc:' + uid).then(function (row) { return (row && row.key) || null; }); }

    // Local shell user so the app works offline; real validation is the
    // server's. salt='cloud' marks accounts whose password is cloud-checked.
    function getOrCreateLocalUser(username) {
        return findUserByName(username).then(function (u) {
            if (u) return u;
            return openDb().then(function (d) {
                return new Promise(function (resolve, reject) {
                    var t = d.transaction(STORE_USERS, 'readwrite');
                    var r = t.objectStore(STORE_USERS).add({
                        username: username, usernameLower: username.toLowerCase(),
                        salt: 'cloud', hash: 'cloud', createdAt: new Date().toISOString()
                    });
                    r.onsuccess = function () { resolve({ id: r.result, username: username }); };
                    r.onerror = function () { reject(r.error); };
                });
            });
        });
    }

    function markUserCloud(uid, authHex) {
        return openDb().then(function (d) {
            return new Promise(function (resolve, reject) {
                var t = d.transaction(STORE_USERS, 'readwrite');
                var r = t.objectStore(STORE_USERS).get(uid);
                r.onsuccess = function () {
                    var u = r.result;
                    if (u) { u.salt = 'cloud'; u.authHash = authHex; t.objectStore(STORE_USERS).put(u); }
                    resolve();
                };
                r.onerror = function () { reject(r.error); };
            });
        });
    }

    function applyCloudPayload(uid, payload) {
        var fp = JSON.stringify(payload && payload.tables);
        var readOps = TABLES.map(function (tb) {
            return dbGet(STORE_DATA, dataKey(uid, tb)).then(function (row) { return { tb: tb, rows: (row && row.rows) || [] }; });
        });
        return Promise.all(readOps).then(function (locals) {
            var changed = false;
            var ops = locals.map(function (t) {
                var cloudRows = (payload.tables && payload.tables[t.tb]) || [];
                var merged = mergeRowsById(t.rows, cloudRows);
                if (JSON.stringify(merged) !== JSON.stringify(t.rows)) changed = true;
                return dbPut(STORE_DATA, { k: dataKey(uid, t.tb), rows: merged });
            });
            if (payload.settings) ops.push(dbPut(STORE_KV, { k: settingsKey(uid), v: payload.settings }));
            return Promise.all(ops).then(function () {
                _lastAppliedFp = fp;    // stop re-processing this exact payload
                return changed;
            });
        });
    }

    // Union of local + cloud rows by id; the cloud copy wins conflicts (it is
    // the union computed by whichever device pushed last, or a newer edit).
    function mergeRowsById(localRows, cloudRows) {
        var seen = {};
        var out = [];
        (cloudRows || []).forEach(function (r) {
            if (r && r.id != null) { seen[r.id] = true; out.push(r); }
        });
        (localRows || []).forEach(function (r) {
            if (r && r.id != null && !seen[r.id]) out.push(r);
        });
        return out;
    }

    function pullCloudData(uid, encKey) {
        return getCloudToken(uid).then(function (token) {
            if (!token) return;
            return cloudApi('/data', { headers: { Authorization: 'Bearer ' + token } }).then(function (res) {
                if (!res.ok || !res.blob) return;
                return decryptPayload(encKey, res.blob).then(function (payload) {
                    return applyCloudPayload(uid, payload);
                });
            });
        })['catch'](function () { /* offline: local cache stands */ });
    }

    var _pushTimer = null;
    var _lastAppliedFp = null;   // payload fingerprint already merged/applied
    function scheduleCloudPush() {
        if (!SYNC_SERVER) return;
        if (_pushTimer) clearTimeout(_pushTimer);
        _pushTimer = setTimeout(pushAllToCloud, 2500);
    }
    function pushAllToCloud() {
        if (!SYNC_SERVER || !_currentUserId) return Promise.resolve();
        var uid = _currentUserId;
        return getCloudToken(uid).then(function (token) {
            if (!token) return;
            return dbBridge.LoadAll().then(function (allJson) {
                return dbGet(STORE_KV, settingsKey(uid)).then(function (row) {
                    var payload = { tables: JSON.parse(allJson), settings: (row && row.v) || {} };
                    return getCloudEncKey(uid).then(function (encKey) {
                        if (!encKey) return;
                        // Pull-merge BEFORE pushing: this browser's snapshot may be
                        // stale (rows deleted here can still exist in the cloud from
                        // another device). Merging first prevents resurrecting them.
                        return cloudApi('/data', { headers: { Authorization: 'Bearer ' + token } }).then(function (cur) {
                            if (cur && cur.ok && cur.blob) {
                                return decryptPayload(encKey, cur.blob).then(function (cloudPayload) {
                                    return applyCloudPayload(uid, cloudPayload).then(function () {
                                        return dbBridge.LoadAll().then(function (freshJson) {
                                            payload.tables = JSON.parse(freshJson);
                                        });
                                    });
                                });
                            }
                        }).then(function () {
                            return encryptPayload(encKey, payload).then(function (blob) {
                                return cloudApi('/data', { method: 'PUT', headers: { Authorization: 'Bearer ' + token }, body: JSON.stringify({ blob: blob }) }).then(function (res) {
                                    if (res && res.ok) {
                                        _lastAppliedFp = JSON.stringify(payload.tables);   // server now holds exactly this
                                    }
                                });
                            });
                        });
                    });
                });
            });
        })['catch'](function () { /* offline; retried on next save */ });
    }

    function cloudSignUp(username, pass) {
        return deriveCloudKeys(username, pass).then(function (keys) {
            return cloudApi('/signup', { method: 'POST', body: JSON.stringify({ username: username, authHash: keys.authHex }) })
                .then(function (res) {
                    if (!res.ok) return { ok: false, error: res.error || 'Sign-up failed.' };
                    return getOrCreateLocalUser(username).then(function (u) {
                        return saveCloudToken(u.id, res.token)
                            .then(function () { return dbPut(STORE_KV, { k: 'cloudenc:' + u.id, key: keys.encKey }); })
                            .then(function () { return markUserCloud(u.id, keys.authHex); })
                            .then(function () { return { ok: true, userId: u.id }; });
                    });
                });
        })['catch'](function (e) { return { ok: false, error: (e && e.message) || 'Sync server unreachable.' }; });
    }

    function cloudSignIn(username, pass) {
        return deriveCloudKeys(username, pass).then(function (keys) {
            return cloudApi('/signin', { method: 'POST', body: JSON.stringify({ username: username, authHash: keys.authHex }) })
                .then(function (res) {
                    if (!res.ok) return { ok: false, error: res.error || 'Sign-in failed.' };
                    return getOrCreateLocalUser(username).then(function (u) {
                        return saveCloudToken(u.id, res.token)
                            .then(function () { return dbPut(STORE_KV, { k: 'cloudenc:' + u.id, key: keys.encKey }); })
                            .then(function () { return markUserCloud(u.id, keys.authHex); })
                            .then(function () { return pullCloudData(u.id, keys.encKey); })
                            .then(function () { return { ok: true, userId: u.id }; });
                    });
                });
        });
    }

    function changeCloudPassword(uid, user, currentPassword, newPassword) {
        if (!newPassword || newPassword.length < 6) return Promise.resolve(fail('New password must be at least 6 characters.'));
        if (currentPassword === newPassword) return Promise.resolve(fail('Choose a password different from the current one.'));
        return deriveCloudKeys(user.username, currentPassword || '').then(function (oldKeys) {
            if (oldKeys.authHex !== user.authHash) return fail('Current password is incorrect.');
            return deriveCloudKeys(user.username, newPassword).then(function (newKeys) {
                return getCloudToken(uid).then(function (token) {
                    if (!token) return fail('Not signed in to the sync server.');
                    return cloudApi('/password', { method: 'POST', headers: { Authorization: 'Bearer ' + token }, body: JSON.stringify({ newAuthHash: newKeys.authHex }) })
                        .then(function (res) {
                            if (!res.ok) return fail(res.error || 'Password change failed.');
                            user.authHash = newKeys.authHex;
                            return dbPut(STORE_USERS, user)
                                .then(function () { return dbPut(STORE_KV, { k: 'cloudenc:' + uid, key: newKeys.encKey }); })
                                .then(function () { return pushAllToCloud(); })
                                .then(function () { return JSON.stringify({ ok: true }); });
                        });
                });
            });
        })['catch'](function (e) { return fail((e && e.message) || 'Password change failed.'); });
    }

    // ---------------------------------------------------------------- users
    function findUserByName(username) {
        var lower = String(username || '').trim().toLowerCase();
        return openDb().then(function (d) {
            return new Promise(function (resolve, reject) {
                var t = d.transaction(STORE_USERS, 'readonly');
                var idx = t.objectStore(STORE_USERS).index('usernameLower');
                var r = idx.get(lower);
                r.onsuccess = function () { resolve(r.result || null); };
                r.onerror = function () { reject(r.error); };
            });
        });
    }

    function loadSession() {
        return dbGet(STORE_KV, 'session').then(function (row) {
            _currentUserId = (row && row.userId) || 0;
            return _currentUserId;
        });
    }
    function saveSession(userId) {
        _currentUserId = userId || 0;
        return userId ? dbPut(STORE_KV, { k: 'session', userId: userId }) : dbDelete(STORE_KV, 'session');
    }

    // Session-restore promise: started at script load so bridge calls that
    // race the page's window.onload (LoadAll can fire before DOMContentLoaded
    // handlers run) always wait for the persisted session to be re-applied.
    var _sessionReady = null;
    function sessionReady() {
        if (!_sessionReady) { _sessionReady = loadSession(); }
        return _sessionReady;
    }
    sessionReady();   // kick off immediately at script load

    // ------------------------------------------------------------- overlays
    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    }

    function baseOverlay(titleText, subtitleText) {
        var wrap = el('div');
        wrap.style.cssText = 'position:fixed;inset:0;z-index:99999;display:flex;align-items:center;justify-content:center;' +
            'background:#090D16;font-family:"Plus Jakarta Sans",system-ui,sans-serif;';
        var card = el('div', 'w-96 max-w-[92vw] bg-slate-900 border border-slate-800 rounded-2xl p-8 shadow-2xl');
        var logo = el('div', 'flex items-center gap-3 mb-2');
        var dot = el('div', 'w-10 h-10 rounded-xl bg-indigo-600 flex items-center justify-center text-white font-bold text-lg');
        dot.textContent = 'M';
        logo.appendChild(dot);
        var ttl = el('div');
        ttl.appendChild(el('div', 'text-xl font-bold text-white', titleText));
        ttl.appendChild(el('div', 'text-xs text-slate-400', subtitleText));
        logo.appendChild(ttl);
        card.appendChild(logo);
        wrap.appendChild(card);
        return { wrap: wrap, card: card };
    }

    function field(labelText, type, id) {
        var box = el('div', 'mb-3');
        var lab = el('label', 'block text-xs text-slate-400 mb-1', labelText);
        lab.htmlFor = id;
        var inp = el('input');
        inp.id = id; inp.type = type; inp.autocomplete = 'off'; inp.spellcheck = false;
        inp.className = 'w-full bg-slate-800 border border-slate-700 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-indigo-500';
        box.appendChild(lab); box.appendChild(inp);
        return { box: box, input: inp };
    }

    function errLine(card) {
        var e = el('div', 'text-xs text-rose-400 min-h-[16px] mb-2');
        card.appendChild(e);
        return e;
    }

    function busyBtn(text) {
        var b = el('button', 'w-full py-2.5 rounded-xl bg-indigo-600 hover:bg-indigo-500 text-white font-semibold text-sm transition disabled:opacity-50');
        b.type = 'button'; b.textContent = text;
        return b;
    }

    function showAuthGate() {
        var ui = baseOverlay('Maridew Finance', SYNC_SERVER
            ? 'Browser edition - encrypted sync across your devices.'
            : 'Browser edition - your data stays in this browser.');
        var card = ui.card;

        var tabs = el('div', 'flex gap-2 my-5');
        var tabIn = el('button', 'flex-1 py-2 rounded-lg text-xs font-semibold bg-indigo-600/20 text-indigo-300 border border-indigo-500/30');
        tabIn.textContent = 'Sign In';
        var tabUp = el('button', 'flex-1 py-2 rounded-lg text-xs font-semibold text-slate-400 border border-transparent hover:text-slate-200');
        tabUp.textContent = 'Create Account';
        tabs.appendChild(tabIn); tabs.appendChild(tabUp);
        card.appendChild(tabs);

        var userF = field('Username', 'text', 'mw-gate-user');
        var passF = field('Password', 'password', 'mw-gate-pass');
        var confF = field('Confirm password', 'password', 'mw-gate-confirm');
        confF.box.style.display = 'none';
        card.appendChild(userF.box); card.appendChild(passF.box); card.appendChild(confF.box);

        var err = errLine(card);
        var go = busyBtn('Sign In');
        card.appendChild(go);
        card.appendChild(el('div', 'text-[11px] text-slate-500 mt-4 leading-relaxed',
            SYNC_SERVER
                ? 'Accounts sync securely across devices. Data is encrypted in your browser before upload - the server can never read it.'
                : 'Accounts live in this browser only (IndexedDB) - the same username on another device is a separate account. Use Settings > Export to move data.'));

        var mode = 'in';
        function setMode(m) {
            mode = m;
            confF.box.style.display = m === 'up' ? '' : 'none';
            tabIn.className = m === 'in'
                ? 'flex-1 py-2 rounded-lg text-xs font-semibold bg-indigo-600/20 text-indigo-300 border border-indigo-500/30'
                : 'flex-1 py-2 rounded-lg text-xs font-semibold text-slate-400 border border-transparent hover:text-slate-200';
            tabUp.className = m === 'up'
                ? 'flex-1 py-2 rounded-lg text-xs font-semibold bg-indigo-600/20 text-indigo-300 border border-indigo-500/30'
                : 'flex-1 py-2 rounded-lg text-xs font-semibold text-slate-400 border border-transparent hover:text-slate-200';
            go.textContent = m === 'in' ? 'Sign In' : 'Create Account';
            err.textContent = '';
        }
        tabIn.onclick = function () { setMode('in'); };
        tabUp.onclick = function () { setMode('up'); };

        function submit() {
            var username = userF.input.value.trim();
            var pass = passF.input.value;
            err.textContent = '';
            go.disabled = true;
            var work;
            if (mode === 'up') {
                work = createAccount(username, pass, confF.input.value);
            } else {
                work = signIn(username, pass);
            }
            work.then(function (res) {
                if (res.ok) {
                    return saveSession(res.userId).then(function () { window.location.reload(); });
                }
                err.textContent = res.error || 'That did not work.';
                go.disabled = false;
            })['catch'](function (e) {
                err.textContent = (e && e.message) || 'Unexpected error.';
                go.disabled = false;
            });
        }
        go.onclick = submit;
        card.addEventListener('keydown', function (ev) { if (ev.key === 'Enter') submit(); });

        document.body.appendChild(ui.wrap);
        setTimeout(function () { userF.input.focus(); }, 50);
    }

    function createAccount(username, pass, confirm) {
        if (!username || username.length < 3) return Promise.resolve({ ok: false, error: 'Username must be at least 3 characters.' });
        if (!pass || pass.length < 6) return Promise.resolve({ ok: false, error: 'Password must be at least 6 characters.' });
        if (pass !== confirm) return Promise.resolve({ ok: false, error: 'Passwords do not match.' });
        if (SYNC_SERVER) return cloudSignUp(username, pass);
        return localCreateAccount(username, pass);
    }

    function localCreateAccount(username, pass) {
        return findUserByName(username).then(function (existing) {
            if (existing) return { ok: false, error: 'That username is already taken.' };
            var salt = newSalt();
            return hashPassword(pass, salt).then(function (hash) {
                var user = {
                    username: username,
                    usernameLower: username.toLowerCase(),
                    salt: salt, hash: hash,
                    createdAt: new Date().toISOString()
                };
                return openDb().then(function (d) {
                    return new Promise(function (resolve, reject) {
                        var t = d.transaction(STORE_USERS, 'readwrite');
                        var r = t.objectStore(STORE_USERS).add(user);
                        r.onsuccess = function () { resolve({ ok: true, userId: r.result }); };
                        r.onerror = function () { reject(r.error); };
                    });
                });
            });
        });
    }

    function signIn(username, pass) {
        if (SYNC_SERVER) {
            return cloudSignIn(username, pass)['catch'](function (e) {
                return localSignIn(username, pass).then(function (res) {
                    if (res.ok) return res;
                    return { ok: false, error: 'Sync server unreachable (' + ((e && e.message) || 'offline') + ') and no matching local account.' };
                });
            });
        }
        return localSignIn(username, pass);
    }

    function localSignIn(username, pass) {
        return findUserByName(username).then(function (u) {
            if (!u) return { ok: false, error: 'No account named "' + username + '" in THIS browser. Accounts live per-browser: new here? Tap "Create Account" to register, or use Settings > Export in the browser where your data lives.' };
            return hashPassword(pass, u.salt).then(function (hash) {
                if (hash !== u.hash) return { ok: false, error: 'Incorrect password.' };
                return { ok: true, userId: u.id };
            });
        });
    }

    function showLockScreen() {
        if (document.getElementById('mw-lock')) return;
        var ui = baseOverlay('Locked', 'Enter your password to unlock.');
        ui.wrap.id = 'mw-lock';
        var card = ui.card;
        var passF = field('Password', 'password', 'mw-lock-pass');
        passF.box.classList.add('mt-4');
        card.appendChild(passF.box);
        var err = errLine(card);
        var go = busyBtn('Unlock');
        card.appendChild(go);

        function attempt() {
            var u = null;
            openDb().then(function (d) {
                return new Promise(function (res, rej) {
                    var t = d.transaction(STORE_USERS, 'readonly');
                    var r = t.objectStore(STORE_USERS).get(_currentUserId);
                    r.onsuccess = function () { res(r.result); };
                    r.onerror = function () { rej(r.error); };
                });
            }).then(function (user) {
                u = user;
                if (user.salt === 'cloud') {
                    // Cloud account: verify against the sync server.
                    return cloudSignIn(user.username, passF.input.value).then(function (res) {
                        return res.ok;
                    })['catch'](function () { return false; });
                }
                return hashPassword(passF.input.value, user.salt).then(function (hash) {
                    return hash === u.hash;
                });
            }).then(function (ok) {
                if (ok) {
                    ui.wrap.remove();
                } else {
                    err.textContent = 'Incorrect password.';
                    passF.input.value = '';
                    passF.input.focus();
                }
            })['catch'](function (e) { err.textContent = (e && e.message) || 'Unlock failed.'; });
        }
        go.onclick = attempt;
        card.addEventListener('keydown', function (ev) { if (ev.key === 'Enter') attempt(); });

        document.body.appendChild(ui.wrap);
        setTimeout(function () { passF.input.focus(); }, 50);
    }

    // -------------------------------------------------------- dbBridge shim
    function loadSettingsObj() {
        var uid = requireUser();
        return dbGet(STORE_KV, settingsKey(uid)).then(function (row) {
            return (row && row.v) || {};
        });
    }
    function saveSettingsObj(obj) {
        var uid = requireUser();
        return dbPut(STORE_KV, { k: settingsKey(uid), v: obj });
    }

    var dbBridge = {
        LoadAll: function () {
            return withUser(function (uid) {
            var out = {};
            return openDb().then(function (d) {
                return new Promise(function (resolve, reject) {
                    var t = d.transaction(STORE_DATA, 'readonly');
                    var s = t.objectStore(STORE_DATA);
                    var remaining = TABLES.length;
                    TABLES.forEach(function (tb) {
                        var r = s.get(dataKey(uid, tb));
                        r.onsuccess = function () { out[tb] = (r.result && r.result.rows) || []; if (--remaining === 0) resolve(null); };
                        r.onerror = function () { reject(r.error); };
                    });
                });
            }).then(function () { return JSON.stringify(out); });
            });
        },
        SaveTable: function (table, jsonArrayJson) {
            return withUser(function (uid) {
            if (TABLES.indexOf(table) < 0) return Promise.reject(new Error("Unknown table '" + table + "'."));
            var rows = JSON.parse(jsonArrayJson);
            return dbPut(STORE_DATA, { k: dataKey(uid, table), rows: rows }).then(function () {
                scheduleCloudPush();
            });
            });
        },
        LoadSettings: function () {
            return withUser(loadSettingsObj).then(function (o) { return JSON.stringify(o); });
        },
        SaveSetting: function (key, value) {
            return withUser(loadSettingsObj).then(function (o) {
                o[key] = value;
                return saveSettingsObj(o);
            }).then(function () { return ''; });
        },
        GetCurrentUserName: function () {
            return withUser(function (uid) {
                return openDb().then(function (d) {
                    return new Promise(function (resolve, reject) {
                        var t = d.transaction(STORE_USERS, 'readonly');
                        var r = t.objectStore(STORE_USERS).get(uid);
                        r.onsuccess = function () { resolve((r.result && r.result.username) || ''); };
                        r.onerror = function () { reject(r.error); };
                    });
                });
            });
        },
        GetDatabasePath: function () {
            return Promise.resolve('IndexedDB - stored in this browser on this device');
        },
        ChangePassword: function (currentPassword, newPassword) {
            return withUser(function (uid) {
            return openDb().then(function (d) {
                return new Promise(function (resolve, reject) {
                    var t = d.transaction(STORE_USERS, 'readonly');
                    var r = t.objectStore(STORE_USERS).get(_currentUserId);
                    r.onsuccess = function () { resolve(r.result); };
                    r.onerror = function () { reject(r.error); };
                });
            }).then(function (u) {
                if (!u) return fail('Account not found.');
                if (u.salt === 'cloud') return changeCloudPassword(uid, u, currentPassword, newPassword);
                return hashPassword(currentPassword || '', u.salt).then(function (hash) {
                    if (hash !== u.hash) return fail('Current password is incorrect.');
                    if (!newPassword || newPassword.length < 6) return fail('New password must be at least 6 characters.');
                    if (currentPassword === newPassword) return fail('Choose a password different from the current one.');
                    var salt = newSalt();
                    return hashPassword(newPassword, salt).then(function (newHash) {
                        u.salt = salt; u.hash = newHash;
                        return dbPut(STORE_USERS, u).then(function () {
                            return JSON.stringify({ ok: true });
                        });
                    });
                });
            })['catch'](function (e) { return fail((e && e.message) || 'Change failed.'); });
            });
        },
        ListBackups: function () {
            return Promise.resolve('[]');
        },
        RestoreBackup: function () {
            return Promise.resolve(fail('Automatic backups are not available in the browser. Use Export to save a copy.'));
        },
        OpenBackupFolder: function () { /* no-op in browser */ },
        ExportDatabase: function () {
            return withUser(function () {
            return dbBridge.LoadAll().then(function (allJson) {
                var payload = {
                    meta: 'maridew-web-export',
                    version: 1,
                    exportedAt: new Date().toISOString(),
                    appVersion: WEB_VERSION,
                    tables: JSON.parse(allJson),
                    settings: null
                };
                return loadSettingsObj().then(function (s) {
                    payload.settings = s;
                    var name = 'MaridewFinance-web-' + new Date().toISOString().slice(0, 10) + '.json';
                    var blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
                    var a = document.createElement('a');
                    a.href = URL.createObjectURL(blob);
                    a.download = name;
                    document.body.appendChild(a);
                    a.click();
                    setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 2000);
                    return JSON.stringify({ ok: true, path: name });
                });
            })['catch'](function (e) { return fail((e && e.message) || 'Export failed.'); });
            });
        },
        ImportDatabase: function () {
            return new Promise(function (resolve) {
                var inp = document.createElement('input');
                inp.type = 'file';
                inp.accept = '.json,application/json';
                inp.onchange = function () {
                    var f = inp.files && inp.files[0];
                    if (!f) { resolve(JSON.stringify({ ok: false, cancelled: true })); return; }
                    var fr = new FileReader();
                    fr.onload = function () {
                        var payload;
                        try { payload = JSON.parse(fr.result); } catch (e) { resolve(fail('That file is not valid JSON.')); return; }
                        if (!payload || payload.meta !== 'maridew-web-export' || !payload.tables) {
                            resolve(fail('That file is not a Maridew Finance web export.'));
                            return;
                        }
                        var ops = TABLES.map(function (tb) {
                            return withUser(function (uid) {
                                return dbPut(STORE_DATA, { k: dataKey(uid, tb), rows: payload.tables[tb] || [] });
                            });
                        });
                        ops.push(withUser(saveSettingsObj));
                        Promise.all(ops).then(function () {
                            resolve(JSON.stringify({ ok: true }));
                            setTimeout(function () { window.location.reload(); }, 400);
                        })['catch'](function (e) { resolve(fail((e && e.message) || 'Import failed.')); });
                    };
                    fr.onerror = function () { resolve(fail('Could not read that file.')); };
                    fr.readAsText(f);
                };
                document.body.appendChild(inp);
                inp.style.display = 'none';
                inp.click();
                setTimeout(function () { inp.remove(); }, 60000);
            });
        },
        SignOut: function () { saveSession(0); },
        SignOutToLogin: function () {
            saveSession(0).then(function () { window.location.reload(); });
        },
        RequestLock: function () {
            if (!_currentUserId) return;
            showLockScreen();
        }
    };

    // ---------------------------------------------------- updateBridge shim
    var updateState = 'none';
    var updateMessage = 'You are up to date. (Browser edition updates with the app.)';
    var updateBridge = {
        CheckForUpdates: function () {
            updateState = 'none';
            updateMessage = 'You are up to date. (Browser edition updates with the app.)';
            return Promise.resolve('');
        },
        DownloadUpdate: function () { return Promise.resolve(''); },
        InstallUpdate: function () { return Promise.resolve(''); },
        GetUpdateStatus: function () {
            return Promise.resolve(JSON.stringify({
                state: updateState,
                latest: '',
                notes: '',
                current: WEB_VERSION,
                message: updateMessage
            }));
        }
    };

    // ------------------------------------------------------------- install
    window.maridewDbBridge = dbBridge;
    window.maridewUpdateBridge = updateBridge;
    // Bridge the WebView2 namespace lookup so the page's existing
    // `window.chrome?.webview?.hostObjects?.dbBridge` resolves to us.
    try {
        window.chrome = window.chrome || {};
        window.chrome.webview = window.chrome.webview || {};
        window.chrome.webview.hostObjects = window.chrome.webview.hostObjects || {};
        Object.defineProperty(window.chrome.webview.hostObjects, 'dbBridge', {
            get: function () { return dbBridge; }, configurable: true
        });
        Object.defineProperty(window.chrome.webview.hostObjects, 'updateBridge', {
            get: function () { return updateBridge; }, configurable: true
        });
    } catch (e) {
        // Extremely locked-down environments: fall back to the maridew* names
        // and patch the page-level lookups below.
    }

    var _pollerStarted = false;
    var _lastBlobStr = null;

    function boot() {
        loadSession().then(function (uid) {
            if (uid) { startCloudPoller(uid); return; }   // signed in: page loads normally
            showAuthGate();                  // not signed in: show the gate
        })['catch'](function () { showAuthGate(); });
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }

    // ------------------------------------------------ desktop → web pull loop
    // The desktop app pushes its whole encrypted database after every change;
    // this poller merges new cloud payloads into this browser and nudges the
    // dashboard to re-render when desktop entries actually arrived.
    function startCloudPoller(uid) {
        if (_pollerStarted) return;
        _pollerStarted = true;
        setInterval(function () {
            if (document.hidden) return;                 // skip when tab not visible
            getCloudToken(uid).then(function (token) {
                if (!token) return;
                cloudApi('/data', { headers: { Authorization: 'Bearer ' + token } }).then(function (res) {
                    if (!res.ok || !res.blob) return;
                    var fp = JSON.stringify(res.blob);   // cheap pre-decrypt change check
                    if (fp === _lastBlobStr) return;
                    _lastBlobStr = fp;
                    return getCloudEncKey(uid).then(function (encKey) {
                        if (!encKey) return;
                        return decryptPayload(encKey, res.blob).then(function (payload) {
                            return applyCloudPayload(uid, payload).then(function (changed) {
                                if (changed) {
                                    window.location.reload();            // re-render from merged cache
                                }
                            });
                        });
                    });
                });
            })['catch'](function () { /* offline: try again next tick */ });
        }, 20000);
    }
})();
