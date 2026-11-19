const CACHE_NAME = 'rjam-pos-offline-v1';
const OFFLINE_ASSETS = [
    '/offline-pos.html',
    '/app.css',
    '/js/pos-offline.js',
    '/manifest.json',
    '/images/rjam-logo.png',
    '/images/rjam-logo-light.png',
    '/images/rjam-mark.png',
    '/images/rjam-favicon.png'
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => cache.addAll(OFFLINE_ASSETS))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (event) => {
    const req = event.request;
    if (req.method !== 'GET') return;

    const url = new URL(req.url);
    if (url.origin !== self.location.origin) return;
    if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/_blazor')) return;

    // Navigation requests to /pos or /offline-pos.html: network-first, fall back to offline POS shell when offline
    if (req.mode === 'navigate' && (url.pathname === '/pos' || url.pathname === '/offline-pos.html')) {
        event.respondWith(
            fetch(req).catch(() => caches.match('/offline-pos.html'))
        );
        return;
    }

    // Cached static offline assets: stale-while-revalidate
    if (OFFLINE_ASSETS.includes(url.pathname)) {
        event.respondWith(
            caches.match(req).then((cached) => {
                const networkFetch = fetch(req).then((resp) => {
                    if (resp && resp.ok) {
                        const clone = resp.clone();
                        caches.open(CACHE_NAME).then((cache) => cache.put(req, clone));
                    }
                    return resp;
                }).catch(() => cached);
                return cached || networkFetch;
            })
        );
    }
});
