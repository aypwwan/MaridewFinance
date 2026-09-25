/* Maridew Finance service worker - makes the web edition installable and
 * usable offline. All user data already lives in IndexedDB; this only
 * caches the app shell.
 *
 * Strategy:
 *   vendor/* (tailwind, chart.js, lucide, fonts) - cache-first: they only
 *     change when a release changes them, and the cache version below is
 *     bumped at the same time.
 *   app shell (index.html, *.js, manifest) - network-first with cached
 *     fallback, so updates arrive as soon as the site does but the app
 *     still opens with no network.
 *   Never touches the sync worker (maridew-sync.*), POSTs, or other origins.
 */
const CACHE = 'maridew-shell-v1';
const SHELL = [
    './', './index.html', './sync-core.js', './web-bridge.js',
    './manifest.json', './icons/icon-192.png', './icons/icon-512.png'
];

self.addEventListener('install', (e) => {
    e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
    e.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (e) => {
    const req = e.request;
    if (req.method !== 'GET') return;
    const url = new URL(req.url);
    if (url.origin !== self.location.origin) return;   // sync worker etc.

    // Vendor assets: cache-first (immutable between cache versions).
    if (url.pathname.includes('/vendor/')) {
        e.respondWith(
            caches.match(req).then((hit) => hit || fetch(req).then((res) => {
                const copy = res.clone();
                caches.open(CACHE).then((c) => c.put(req, copy));
                return res;
            }))
        );
        return;
    }

    // App shell: network-first, fall back to the cache (offline).
    e.respondWith(
        fetch(req).then((res) => {
            const copy = res.clone();
            caches.open(CACHE).then((c) => c.put(req, copy));
            return res;
        }).catch(() =>
            caches.match(req).then((hit) => hit || caches.match('./index.html'))
        )
    );
});
