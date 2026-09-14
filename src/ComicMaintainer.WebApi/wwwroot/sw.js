// Service Worker for Comic Maintainer PWA
// Provides basic offline support and caching
// Version is dynamically injected by the server to force browser updates

// Cache version will be determined dynamically from the API
let CACHE_NAME = 'comic-maintainer-v2'; // Default fallback
const CACHE_PREFIX = 'comic-maintainer-';
const urlsToCache = [
  '/manifest.json',
  '/offline.html',
  '/icons/icon-192x192.png',
  '/icons/icon-512x512.png',
  '/icons/icon-192x192-maskable.png',
  '/icons/icon-512x512-maskable.png',
  '/icons/apple-touch-icon.png',
  '/icons/favicon-32x32.png',
  '/icons/favicon-16x16.png'
];

// Fetch current version from API and set cache name
async function updateCacheName() {
  try {
    const response = await fetch('/api/version');
    if (response.ok) {
      const data = await response.json();
      const version = data.version.replace(/\./g, '-'); // Replace dots with dashes for cache name
      CACHE_NAME = `${CACHE_PREFIX}${version}`;
      console.log('Service Worker: Using cache version:', CACHE_NAME);
    }
  } catch (error) {
    console.log('Service Worker: Failed to fetch version, using default cache name', error);
  }
}

// Maximum number of entries kept in the runtime asset cache. Icons, fonts and
// other static assets accumulate over time (especially on long-lived installs),
// so trim the cache to a bounded size using a simple FIFO/LRU-style eviction of
// the oldest inserted entries.
const MAX_RUNTIME_CACHE_ENTRIES = 120;

// Comic covers and page images are content-addressed by file path and do not
// change when the app is upgraded, so they live in their own cache that
// survives version bumps (the versioned cache above is wiped on every
// release). Decoding a cover is by far the most expensive thing the library
// grid does over the network, so serving them cache-first makes scrolling back
// through an already-visited library effectively instant and works offline.
//
// This cache holds *authenticated* responses, so it is cleared on logout via
// the CLEAR_IMAGE_CACHE message.
const IMAGE_CACHE_NAME = `${CACHE_PREFIX}images-v1`;
const MAX_IMAGE_CACHE_ENTRIES = 400;

// Request paths whose GET responses are safe to cache as images.
const IMAGE_API_PATHS = ['/api/comicreader/cover', '/api/comicreader/page'];

function isCacheableImageRequest(request, url) {
  return request.method === 'GET' &&
    IMAGE_API_PATHS.some((path) => url.pathname === path);
}

// Cache-first with a background refresh omitted on purpose: these responses are
// immutable for a given file path, so a cache hit is always correct until the
// underlying file changes (at which point its path/mtime-derived URL changes
// too, or the user can clear the cache).
async function serveImageFromCache(request) {
  const cache = await caches.open(IMAGE_CACHE_NAME);
  const cached = await cache.match(request);
  if (cached) {
    return cached;
  }

  const response = await fetch(request);
  if (response && response.status === 200) {
    // Only successful, non-opaque responses are worth storing.
    cache.put(request, response.clone())
      .then(() => trimCache(IMAGE_CACHE_NAME, MAX_IMAGE_CACHE_ENTRIES))
      .catch(() => { /* quota errors are non-fatal */ });
  }
  return response;
}

async function trimCache(cacheName, maxEntries) {
  try {
    const cache = await caches.open(cacheName);
    const keys = await cache.keys();
    if (keys.length <= maxEntries) {
      return;
    }
    // cache.keys() returns entries in insertion order, so the head of the list
    // is the least recently added entry.
    const excess = keys.length - maxEntries;
    for (let i = 0; i < excess; i++) {
      await cache.delete(keys[i]);
    }
  } catch (error) {
    console.log('Service Worker: Failed to trim cache', cacheName, error);
  }
}

// Serve the precached offline page; fall back to a minimal inline document if
// it is somehow missing from the cache.
async function offlineFallbackResponse() {
  const cached = await caches.match('/offline.html');
  if (cached) {
    return cached;
  }

  return new Response(
    '<html><body><h1>Offline</h1><p>Comic Maintainer is unavailable while offline.</p></body></html>',
    { headers: { 'Content-Type': 'text/html' } }
  );
}

// Install event - cache essential resources
self.addEventListener('install', (event) => {
  console.log('Service Worker: Installing...');
  event.waitUntil(
    updateCacheName()
      .then(() => caches.open(CACHE_NAME))
      .then((cache) => {
        console.log('Service Worker: Caching essential files with cache name:', CACHE_NAME);
        return cache.addAll(urlsToCache);
      })
      .then(() => self.skipWaiting())
  );
});

// Activate event - clean up old caches
self.addEventListener('activate', (event) => {
  console.log('Service Worker: Activating...');
  event.waitUntil(
    updateCacheName()
      .then(() => caches.keys())
      .then((cacheNames) => {
        return Promise.all(
          cacheNames.map((cacheName) => {
            // Delete any cache that starts with our prefix but isn't the current version
            // The image cache is deliberately preserved across releases: it
            // holds comic covers/pages, which are unrelated to the app version.
            if (cacheName.startsWith(CACHE_PREFIX) &&
                cacheName !== CACHE_NAME &&
                cacheName !== IMAGE_CACHE_NAME) {
              console.log('Service Worker: Deleting old cache:', cacheName);
              return caches.delete(cacheName);
            }
          })
        );
      }).then(() => self.clients.claim())
  );
});

// Fetch event - serve from cache when offline, with network-first strategy for API calls
self.addEventListener('fetch', (event) => {
  const { request } = event;
  const url = new URL(request.url);
  const acceptsHtml = request.headers.get('Accept')?.includes('text/html');
  const isNavigationRequest = request.mode === 'navigate' || request.destination === 'document' || acceptsHtml || url.pathname === '/';
  
  // Never cache the service worker file itself
  if (url.pathname === '/sw.js') {
    event.respondWith(fetch(request, { cache: 'no-store' }));
    return;
  }

  // Always fetch the app shell from the network first so users do not need a hard refresh
  if (isNavigationRequest || url.pathname.endsWith('.html')) {
    event.respondWith(
      fetch(request, { cache: 'no-store' }).catch(() => {
        if (request.headers.get('Accept')?.includes('text/html')) {
          return offlineFallbackResponse();
        }

        return new Response('Offline', { status: 503 });
      })
    );
    return;
  }
  
  // Comic covers and page images: cache-first with a bounded cache.
  if (isCacheableImageRequest(request, url)) {
    event.respondWith(
      serveImageFromCache(request).catch(
        () => new Response('Offline', { status: 503 })
      )
    );
    return;
  }

  // Writes (POST/PUT/PATCH/DELETE) must always hit the network and must never
  // be cached or replayed. Let them fall through to the browser untouched
  // rather than wrapping them in an offline fallback that would mask a failed
  // mutation as a successful-looking response.
  if (url.pathname.startsWith('/api/') && request.method !== 'GET') {
    return;
  }

  // Network-first strategy for API calls and dynamic content
  if (url.pathname.startsWith('/api/')) {
    event.respondWith(
      fetch(request)
        .then((response) => {
          // Don't cache API responses (they're dynamic)
          return response;
        })
        .catch(() => {
          // Return a friendly offline message for API calls
          return new Response(
            JSON.stringify({ error: 'Offline - API unavailable' }),
            {
              status: 503,
              headers: { 'Content-Type': 'application/json' }
            }
          );
        })
    );
    return;
  }
  
  // Cache-first for static images (icons, fonts); stale-while-revalidate for CSS/JS
  // so that a newer asset is picked up on the next navigation even if a stale
  // entry slipped into the cache under the same URL.
  const isCssOrJs = url.pathname.startsWith('/css/') || url.pathname.startsWith('/js/');

  if (isCssOrJs) {
    event.respondWith(
      caches.open(CACHE_NAME).then((cache) =>
        cache.match(request).then((cachedResponse) => {
          const networkFetch = fetch(request)
            .then((networkResponse) => {
              if (networkResponse && networkResponse.status === 200 && networkResponse.type === 'basic') {
                cache.put(request, networkResponse.clone())
                  .then(() => trimCache(CACHE_NAME, MAX_RUNTIME_CACHE_ENTRIES));
              }
              return networkResponse;
            })
            .catch(() => cachedResponse);
          // Return cached immediately if present, otherwise wait for network.
          return cachedResponse || networkFetch;
        })
      )
    );
    return;
  }

  // Cache-first strategy for other static assets (icons, fonts, etc.)
  event.respondWith(
    caches.match(request)
      .then((response) => {
        if (response) {
          // Return cached version
          return response;
        }
        
        // Not in cache, fetch from network
        return fetch(request).then((response) => {
          // Don't cache non-successful responses
          if (!response || response.status !== 200 || response.type !== 'basic') {
            return response;
          }
          
          // Clone the response (can only be consumed once)
          const responseToCache = response.clone();
          
          // Cache static assets
          if (url.pathname.startsWith('/icons/')) {
            caches.open(CACHE_NAME).then((cache) =>
              cache.put(request, responseToCache)
                .then(() => trimCache(CACHE_NAME, MAX_RUNTIME_CACHE_ENTRIES))
            );
          }
          
          return response;
        });
      })
      .catch(() => {
        // Return a friendly offline page for HTML requests
        if (request.headers.get('Accept')?.includes('text/html')) {
          return offlineFallbackResponse();
        }

        return new Response('Offline', { status: 503 });
      })
  );
});

// Listen for messages from the client
self.addEventListener('message', (event) => {
  if (!event.data) {
    return;
  }

  if (event.data.type === 'SKIP_WAITING') {
    self.skipWaiting();
    return;
  }

  // Cached covers/pages are authenticated content, so drop them when the user
  // signs out rather than leaving them readable for whoever logs in next.
  if (event.data.type === 'CLEAR_IMAGE_CACHE') {
    event.waitUntil(caches.delete(IMAGE_CACHE_NAME));
  }
});
