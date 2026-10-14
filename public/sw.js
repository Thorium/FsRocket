// FsRocket service worker: makes the installed app start offline.
// Network-first, so online play always gets the latest deploy; every
// same-origin GET that succeeds is copied into the cache, and the cache
// answers when the network fails. The page, its hashed bundles and the .LEV
// maps are all fetched on the first launch, so one online start is enough.
const CACHE = 'fsrocket-v1';

self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(names => Promise.all(names.filter(n => n !== CACHE).map(n => caches.delete(n))))
      .then(() => self.clients.claim()));
});

self.addEventListener('fetch', event => {
  const req = event.request;
  if (req.method !== 'GET' || new URL(req.url).origin !== self.location.origin) return;
  event.respondWith(
    fetch(req)
      .then(res => {
        if (res.ok) {
          const copy = res.clone();
          caches.open(CACHE).then(cache => cache.put(req, copy));
        }
        return res;
      })
      // Offline: the cached copy, or for a page load (e.g. "?touch=1") the app shell
      .catch(() => caches.match(req, { ignoreSearch: req.mode === 'navigate' })
        .then(hit => hit || (req.mode === 'navigate' ? caches.match('./') : undefined))
        .then(hit => hit || Response.error())));
});
