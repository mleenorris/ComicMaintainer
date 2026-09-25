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

// Comics the user explicitly downloaded for offline reading. These live in
// their own cache so they are never evicted by the bounded image cache (a
// download is a deliberate snapshot the user expects to still be there on a
// plane) and survive app version bumps.
//
// Unlike the opportunistic image cache, this cache also survives logout: a
// download is an explicit user action, and signing out (which can happen
// simply because a token expired, including while offline) must not silently
// throw away content the user deliberately saved for a trip. Downloads are
// only removed when the user removes them, via REMOVE_OFFLINE_COMIC.
const OFFLINE_CACHE_NAME = `${CACHE_PREFIX}offline-comics-v1`;

// Synthetic, canonical cache keys for downloaded content. The reader requests
// pages as `/api/ComicReader/page?filePath=...&page=N`, whose casing and
// parameter order are not guaranteed, so entries are keyed by their decoded
// (filePath, kind, page) triple instead of the raw request URL.
const OFFLINE_KEY_PREFIX = '/__offline-comic/';

// Documents needed to open the reader without a network. Navigation requests
// are network-first, but a successful response is copied into the versioned
// cache so the same page can be replayed while offline.
const DOCUMENT_CACHE_PATHS = ['/', '/index.html', '/reader.html', '/offline.html'];

function offlineCacheKey(filePath, kind, page) {
  const suffix = kind === 'page' ? `/page/${page}` : `/${kind}`;
  return `${self.location.origin}${OFFLINE_KEY_PREFIX}${encodeURIComponent(filePath)}${suffix}`;
}

// Map an incoming reader API request onto its offline cache key, or null when
// the request is not something downloads cover.
function offlineKeyForRequest(request, url) {
  if (request.method !== 'GET') {
    return null;
  }

  const pathname = url.pathname.toLowerCase();
  const filePath = url.searchParams.get('filePath') || url.searchParams.get('filepath');
  if (!filePath) {
    return null;
  }

  if (pathname === '/api/comicreader/info') {
    return offlineCacheKey(filePath, 'info');
  }
  if (pathname === '/api/comicreader/cover') {
    return offlineCacheKey(filePath, 'cover');
  }
  if (pathname === '/api/comicreader/page') {
    const page = parseInt(url.searchParams.get('page') || '1', 10);
    if (!Number.isFinite(page) || page < 1) {
      return null;
    }
    return offlineCacheKey(filePath, 'page', page);
  }

  return null;
}

async function matchOfflineComic(request, url) {
  const key = offlineKeyForRequest(request, url);
  if (!key) {
    return null;
  }
  const cache = await caches.open(OFFLINE_CACHE_NAME);
  return (await cache.match(key)) || null;
}

// Navigation responses are cached under their path only: reader.html is always
// requested with a ?file=... query, and every one of those URLs renders the
// same document.
function documentCacheKey(url) {
  return `${url.origin}${url.pathname === '/' ? '/' : url.pathname}`;
}

async function cacheDocumentResponse(url, response) {
  try {
    const cache = await caches.open(CACHE_NAME);
    await cache.put(documentCacheKey(url), response);
    await trimCache(CACHE_NAME, MAX_RUNTIME_CACHE_ENTRIES);
  } catch (error) {
    console.log('Service Worker: Failed to cache document', url.pathname, error);
  }
}

async function matchCachedDocument(url) {
  const cached = await caches.match(documentCacheKey(url));
  return cached || null;
}

// Download every page of a comic (plus its info payload, which the reader
// needs before it can render anything) into the offline cache. Progress is
// reported back to the requesting page so it can show a progress indicator.
async function downloadComicForOffline(client, options) {
  const filePath = options && options.filePath;
  const headers = (options && options.headers) || {};
  if (!filePath) {
    return;
  }

  const post = (message) => {
    if (client && typeof client.postMessage === 'function') {
      client.postMessage(Object.assign({ filePath }, message));
    }
  };

  const fetchOptions = { credentials: 'include', headers };
  const cache = await caches.open(OFFLINE_CACHE_NAME);

  const fetchAndStore = async (requestUrl, key) => {
    const response = await fetch(requestUrl, fetchOptions);
    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}`);
    }
    await cache.put(key, response.clone());
    return response;
  };

  try {
    const infoResponse = await fetchAndStore(
      `/api/ComicReader/info?filePath=${encodeURIComponent(filePath)}`,
      offlineCacheKey(filePath, 'info')
    );
    const info = await infoResponse.json();
    const totalPages = Number(info.pageCount) || 0;
    if (totalPages < 1) {
      throw new Error('Comic reports no pages');
    }

    post({ type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 0, total: totalPages });

    for (let page = 1; page <= totalPages; page++) {
      await fetchAndStore(
        `/api/ComicReader/page?filePath=${encodeURIComponent(filePath)}&page=${page}`,
        offlineCacheKey(filePath, 'page', page)
      );
      post({ type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: page, total: totalPages });
    }

    // The cover is what the offline list renders; failing to get it must not
    // invalidate an otherwise complete download.
    try {
      await fetchAndStore(
        `/api/ComicReader/cover?filePath=${encodeURIComponent(filePath)}`,
        offlineCacheKey(filePath, 'cover')
      );
    } catch (error) {
      console.log('Service Worker: Failed to cache cover for offline comic', error);
    }

    post({ type: 'OFFLINE_DOWNLOAD_COMPLETE', total: totalPages, fileName: info.fileName });
  } catch (error) {
    // A partial download is useless and would masquerade as a complete one, so
    // roll it back.
    await removeOfflineComic(filePath);
    post({ type: 'OFFLINE_DOWNLOAD_FAILED', error: String(error && error.message ? error.message : error) });
  }
}

async function removeOfflineComic(filePath) {
  const cache = await caches.open(OFFLINE_CACHE_NAME);
  const prefix = `${self.location.origin}${OFFLINE_KEY_PREFIX}${encodeURIComponent(filePath)}/`;
  const keys = await cache.keys();
  await Promise.all(
    keys
      .filter((request) => request.url.startsWith(prefix))
      .map((request) => cache.delete(request))
  );
}

// Summarise the offline cache as one entry per downloaded comic so a client
// (the reader button, the offline page) can render what is available.
async function listOfflineComics() {
  const cache = await caches.open(OFFLINE_CACHE_NAME);
  const keys = await cache.keys();
  const prefix = `${self.location.origin}${OFFLINE_KEY_PREFIX}`;
  const comics = new Map();

  for (const request of keys) {
    if (!request.url.startsWith(prefix)) {
      continue;
    }
    const remainder = request.url.slice(prefix.length);
    const separator = remainder.indexOf('/');
    if (separator < 0) {
      continue;
    }
    let filePath;
    try {
      filePath = decodeURIComponent(remainder.slice(0, separator));
    } catch (error) {
      continue;
    }
    const entry = comics.get(filePath) || { filePath, pageCount: 0 };
    if (remainder.slice(separator + 1).startsWith('page/')) {
      entry.pageCount++;
    }
    comics.set(filePath, entry);
  }

  return Array.from(comics.values());
}

function replyToMessage(event, payload) {
  const port = event.ports && event.ports[0];
  if (port) {
    port.postMessage(payload);
    return;
  }
  if (event.source && typeof event.source.postMessage === 'function') {
    event.source.postMessage(payload);
  }
}

function isCacheableImageRequest(request, url) {
  const pathname = url.pathname.toLowerCase();
  return request.method === 'GET' &&
    IMAGE_API_PATHS.some((path) => pathname === path);
}

// Scope an image cache entry to the requesting session: the cache is shared
// across logins on the same device, so without this a cover/page fetched by
// one authenticated user could be served straight out of the cache to
// whoever signs in next. The client sends a per-login opaque id (regenerated
// on logout) in X-Client-Session; folding it into the cache key means a
// different session simply misses the cache instead of reusing someone
// else's cached response.
function cacheKeyForRequest(request) {
  const sessionId = request.headers.get('X-Client-Session');
  if (!sessionId) {
    return request;
  }
  const keyUrl = new URL(request.url);
  keyUrl.searchParams.set('__sid', sessionId);
  return new Request(keyUrl.toString(), { method: request.method });
}

// Cache-first, but always revalidated in the background: covers/pages are
// served from cache immediately for speed, while a network fetch runs
// alongside to refresh the entry if the underlying file changed, or evict it
// if the session backing it is no longer authorized (401/403). This keeps
// the fast path but stops it from serving indefinitely-stale or
// no-longer-authorized content.
async function serveImageFromCache(request) {
  const cache = await caches.open(IMAGE_CACHE_NAME);
  const cacheKey = cacheKeyForRequest(request);
  const cached = await cache.match(cacheKey);

  const networkFetch = fetch(request).then((response) => {
    if (response && response.status === 200) {
      // Only successful, non-opaque responses are worth storing.
      cache.put(cacheKey, response.clone())
        .then(() => trimCache(IMAGE_CACHE_NAME, MAX_IMAGE_CACHE_ENTRIES))
        .catch(() => { /* quota errors are non-fatal */ });
    } else if (response && (response.status === 401 || response.status === 403)) {
      // No longer authorized for this entry; stop serving it from cache.
      cache.delete(cacheKey).catch(() => { /* best effort */ });
    }
    return response;
  });

  if (cached) {
    // Serve the cached copy immediately; let the revalidation above update
    // or evict the entry in the background. A network failure here just
    // means we keep serving the (still the best available) cached copy.
    networkFetch.catch(() => { /* offline; keep serving the cached entry */ });
    return cached;
  }

  return networkFetch;
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
            // holds comic covers/pages, which are unrelated to the app
            // version. The offline cache holds user-requested downloads and
            // must likewise never be discarded by an upgrade.
            if (cacheName.startsWith(CACHE_PREFIX) &&
                cacheName !== CACHE_NAME &&
                cacheName !== IMAGE_CACHE_NAME &&
                cacheName !== OFFLINE_CACHE_NAME) {
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

  // Writes (POST/PUT/PATCH/DELETE) must always hit the network and must never
  // be cached or replayed. Let them fall through to the browser untouched
  // rather than wrapping them in an offline fallback that would mask a failed
  // mutation as a successful-looking response. This must run before the
  // navigation check below: a non-GET API request that sends
  // "Accept: text/html" would otherwise satisfy isNavigationRequest and be
  // handled by the offline-HTML fallback instead of being allowed to fail
  // normally.
  if (url.pathname.startsWith('/api/') && request.method !== 'GET') {
    return;
  }

  // Always fetch the app shell from the network first so users do not need a hard refresh
  if (isNavigationRequest || url.pathname.endsWith('.html')) {
    const isCacheableDocument = request.method === 'GET' && DOCUMENT_CACHE_PATHS.includes(url.pathname);
    event.respondWith(
      fetch(request, { cache: 'no-store' })
        .then((response) => {
          // Keep a copy of the app shell so pages downloaded for offline
          // reading can actually be opened without a network.
          if (isCacheableDocument && response && response.ok && response.type === 'basic') {
            const copy = response.clone();
            event.waitUntil(cacheDocumentResponse(url, copy));
          }
          return response;
        })
        .catch(async () => {
          if (isCacheableDocument) {
            const cachedDocument = await matchCachedDocument(url);
            if (cachedDocument) {
              return cachedDocument;
            }
          }

          if (request.headers.get('Accept')?.includes('text/html')) {
            return offlineFallbackResponse();
          }

          return new Response('Offline', { status: 503 });
        })
    );
    return;
  }

  // Comics explicitly downloaded for offline reading are served straight from
  // their own cache, ahead of the network, so a saved comic reads identically
  // with or without a connection.
  const offlineKey = offlineKeyForRequest(request, url);
  if (offlineKey) {
    event.respondWith(
      matchOfflineComic(request, url)
        .then((cached) => cached || null)
        .catch(() => null)
        .then((cached) => {
          if (cached) {
            return cached;
          }
          if (isCacheableImageRequest(request, url)) {
            return serveImageFromCache(request).catch(
              () => new Response('Offline', { status: 503 })
            );
          }
          return fetch(request).catch(() => new Response(
            JSON.stringify({ error: 'Offline - API unavailable' }),
            { status: 503, headers: { 'Content-Type': 'application/json' } }
          ));
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

  // Cached covers/pages are authenticated content that the user never asked
  // to keep, so drop them when the user signs out rather than leaving them
  // readable for whoever logs in next. Explicit offline downloads are
  // deliberately *not* cleared here — see OFFLINE_CACHE_NAME above.
  if (event.data.type === 'CLEAR_IMAGE_CACHE') {
    event.waitUntil(caches.delete(IMAGE_CACHE_NAME));
    return;
  }

  // Download a comic for offline reading. Auth headers are supplied by the
  // page because the worker has no access to the token in localStorage.
  if (event.data.type === 'DOWNLOAD_COMIC') {
    event.waitUntil(downloadComicForOffline(event.source, event.data));
    return;
  }

  if (event.data.type === 'REMOVE_OFFLINE_COMIC') {
    event.waitUntil(
      removeOfflineComic(event.data.filePath)
        .then(() => replyToMessage(event, {
          type: 'OFFLINE_COMIC_REMOVED',
          filePath: event.data.filePath
        }))
    );
    return;
  }

  if (event.data.type === 'LIST_OFFLINE_COMICS') {
    event.waitUntil(
      listOfflineComics().then((comics) => replyToMessage(event, {
        type: 'OFFLINE_COMICS',
        comics
      }))
    );
  }
});
