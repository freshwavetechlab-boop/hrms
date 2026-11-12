const CACHE_PREFIX = 'frevo-one-ess-'
const CACHE_NAME = `${CACHE_PREFIX}v5`
const APP_SHELL = ['/', '/index.html', '/favicon.svg', '/manifest.webmanifest', '/pwa-192x192.png', '/pwa-512x512.png', '/pwa-maskable.svg', '/assets/FrevoOneLogo.png']

self.addEventListener('install', event => {
  // Let an update wait for current app windows to close. Never replace the
  // worker or force a reload while an employee is submitting attendance.
  event.waitUntil(caches.open(CACHE_NAME).then(cache => cache.addAll(APP_SHELL)))
})

self.addEventListener('activate', event => {
  // Other applications can share an origin. Delete only this app's older caches.
  event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith(CACHE_PREFIX) && key !== CACHE_NAME).map(key => caches.delete(key)))).then(() => self.clients.claim()))
})

self.addEventListener('fetch', event => {
  const request = event.request
  const url = new URL(request.url)
  // Attendance and all authenticated/personalized data always go to the server.
  // There is deliberately no offline queue or replay of punches or other writes.
  if (request.method !== 'GET' || url.origin !== self.location.origin || request.headers.has('Authorization') || /^\/api(?:\/|$)/i.test(url.pathname)) return
  if (request.mode === 'navigate') {
    event.respondWith(fetch(request).catch(async () => (await (await caches.open(CACHE_NAME)).match('/index.html')) || Response.error()))
    return
  }
  // Cache public, versioned frontend assets only; never arbitrary downloads,
  // user-specific paths, or URLs carrying query parameters such as access tokens.
  const bundledAsset = /^\/assets\/[^/]+-[A-Za-z0-9_-]{8,}\.(?:js|css|woff2?|ttf|svg|png|jpe?g|webp|gif|ico)$/i.test(url.pathname)
  if (url.search || (!APP_SHELL.includes(url.pathname) && !bundledAsset)) return
  event.respondWith(caches.open(CACHE_NAME).then(async cache => {
    const cached = await cache.match(request)
    if (cached) return cached
    const response = await fetch(request)
    if (response.ok && response.type !== 'opaque' && !/no-store|private/i.test(response.headers.get('Cache-Control') || '')) {
      // A quota/storage error must not turn a successful network request into an error.
      event.waitUntil(cache.put(request, response.clone()).catch(() => undefined))
    }
    return response
  }).catch(() => Response.error()))
})
