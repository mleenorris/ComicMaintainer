// Service Worker for Comic Maintainer PWA
// Provides basic offline support and caching
// Version is dynamically injected by the server to force browser updates

// Cache version will be determined dynamically from the API. Install and
// activate also record the name (see STATE_CACHE_NAME below): the browser
// stops an idle worker and evaluates this script afresh for its next event,
// and that new instance must keep using the same cache, not this default.
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

// The app's classic scripts, in the order index.html loads them (reader.html
// loads /js/shared.js too). Keep in sync with the <script> tags in
// index.html. They are precached under the same ?v=<version> URL the server
// injects into the HTML, so the first navigation after an update is served
// from cache and offline launches have every script. A script that fails to
// precache is fetched (and cached) on demand instead.
const APP_SCRIPTS = [
  '/js/shared.js',
  '/js/app/templating.js',
  '/js/app/api.js',
  '/js/app/state.js',
  '/js/app/events.js',
  '/js/app/shell.js',
  '/js/app/service-worker.js',
  '/js/app/router.js',
  '/js/app/library.js',
  '/js/app/series.js',
  '/js/app/file-list.js',
  '/js/app/file-actions.js',
  '/js/app/jobs.js',
  '/js/app/settings.js',
  '/js/app/scheduled-jobs.js',
  '/js/app/email.js',
  '/js/app/menus.js',
  '/js/app/series-metadata.js',
  '/js/app/modals.js',
  '/js/app/error-reporting.js',
  '/js/main.js'
];

// Page scripts of the other documents that must work offline: reader.html and
// the offline.html fallback each load their own script from /js/pages/. They
// are precached the same way as APP_SCRIPTS.
const PAGE_SCRIPTS = [
  '/js/pages/reader.js',
  '/js/pages/offline.js'
];

// Raw app version (e.g. "2.0.341.0") as reported by /api/version; matches the
// __APP_VERSION__ value the server injects into script URLs.
let APP_VERSION = null;

// Fetch current version from API and set cache name
async function updateCacheName() {
  try {
    const response = await fetch('/api/version');
    if (response.ok) {
      const data = await response.json();
      APP_VERSION = data.version;
      const version = data.version.replace(/\./g, '-'); // Replace dots with dashes for cache name
      CACHE_NAME = `${CACHE_PREFIX}${version}`;
      console.log('Service Worker: Using cache version:', CACHE_NAME);
    }
  } catch (error) {
    console.log('Service Worker: Failed to fetch version, using default cache name', error);
  }
}

// Where install and activate record CACHE_NAME, together with the APP_VERSION
// whose scripts install precached (see precachedUrls). There are two records
// because an update installs (filling its own cache) while the previous
// version is still in control and still serving from its cache.
const STATE_CACHE_NAME = `${CACHE_PREFIX}sw-state-v1`;
const INSTALLED_CACHE_KEY = '/__sw-state/installed-cache';
const ACTIVE_CACHE_KEY = '/__sw-state/active-cache';

// Resolves to CACHE_NAME once it is known in this instance of the worker.
let cacheNameReady = null;

async function recordCacheName(key) {
  cacheNameReady = Promise.resolve(CACHE_NAME);
  try {
    const cache = await caches.open(STATE_CACHE_NAME);
    await cache.put(key, new Response(JSON.stringify({ cacheName: CACHE_NAME, version: APP_VERSION })));
  } catch (error) {
    console.log('Service Worker: Failed to record the cache name', CACHE_NAME, error);
  }
}

async function recallCacheName(key, fallback) {
  try {
    const record = await caches.match(key, { cacheName: STATE_CACHE_NAME });
    if (record) {
      const recorded = await record.json();
      CACHE_NAME = recorded.cacheName;
      APP_VERSION = recorded.version;
      return CACHE_NAME;
    }
  } catch (error) {
    console.log('Service Worker: Failed to read the recorded cache name', error);
  }
  if (fallback) {
    await fallback();
  }
  return CACHE_NAME;
}

// The cache to serve from and add runtime entries to: the one the last
// activation recorded. Read once per instance, without asking the server.
function activeCacheName() {
  if (!cacheNameReady) {
    cacheNameReady = recallCacheName(ACTIVE_CACHE_KEY);
  }
  return cacheNameReady;
}

// The cache this worker's install filled. Activation usually runs in a new
// instance (a waiting update is stopped while idle), where only the record
// tells which cache that was; /api/version is asked only when there is none.
function installedCacheName() {
  if (!cacheNameReady) {
    cacheNameReady = recallCacheName(INSTALLED_CACHE_KEY, updateCacheName);
  }
  return cacheNameReady;
}

// Maximum number of runtime entries kept in the versioned cache. Documents,
// icons, stylesheets and scripts cached as they are fetched accumulate over
// time (especially on long-lived installs), so the oldest added are evicted
// first once there are more. What install precached is never evicted and does
// not count towards the limit (see trimCache).
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
    const cacheName = await activeCacheName();
    const cache = await caches.open(cacheName);
    await cache.put(documentCacheKey(url), response);
    await trimCache(cacheName, MAX_RUNTIME_CACHE_ENTRIES);
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

// Evict the oldest entries beyond maxEntries. What install precached is never
// evicted, and does not count towards maxEntries either: an offline launch
// needs all of it, however many files were cached since. (The image cache holds
// none of it.)
async function trimCache(cacheName, maxEntries) {
  try {
    const cache = await caches.open(cacheName);
    const precached = precachedUrls();
    const keys = (await cache.keys()).filter((request) => !precached.has(request.url));
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

function versionedScriptUrl(script) {
  return `${script}?v=${APP_VERSION}`;
}

// Precache the versioned app and page scripts. Best effort: without a known
// version the URLs the pages will request cannot be predicted, and a failed
// script must not abort installation (cache.addAll would reject on any 404).
function precacheAppScripts(cache) {
  if (!APP_VERSION) {
    return Promise.resolve();
  }
  return Promise.all([...APP_SCRIPTS, ...PAGE_SCRIPTS].map((script) =>
    cache.add(versionedScriptUrl(script)).catch((error) => {
      console.log('Service Worker: Failed to precache', script, error);
    })
  ));
}

// The absolute URLs of everything install puts in the versioned cache. A
// woken-up worker knows them too: recallCacheName restores APP_VERSION.
function precachedUrls() {
  const scripts = APP_VERSION ? [...APP_SCRIPTS, ...PAGE_SCRIPTS].map(versionedScriptUrl) : [];
  return new Set([...urlsToCache, ...scripts].map((path) => new URL(path, self.location.origin).href));
}

// Install event - cache essential resources
//
// An update deliberately does not call skipWaiting() here. It waits until the
// user accepts the "new version available" banner in main.js, which sends
// SKIP_WAITING (see the message handler below). Taking over straight away
// would swap the worker under an open page and reload it via
// 'controllerchange' without warning, interrupting work in progress. The very
// first install is unaffected: with no active worker to replace, it activates
// immediately.
self.addEventListener('install', (event) => {
  console.log('Service Worker: Installing...');
  event.waitUntil(
    updateCacheName()
      .then(() => caches.open(CACHE_NAME))
      .then((cache) => {
        console.log('Service Worker: Caching essential files with cache name:', CACHE_NAME);
        return cache.addAll(urlsToCache).then(() => precacheAppScripts(cache));
      })
      .then(() => recordCacheName(INSTALLED_CACHE_KEY))
  );
});

// Activate event - clean up old caches
//
// The cache to keep is the one this worker's install filled, even when the
// server is unreachable by now or already reports a newer release.
self.addEventListener('activate', (event) => {
  console.log('Service Worker: Activating...');
  event.waitUntil(
    installedCacheName()
      .then(() => recordCacheName(ACTIVE_CACHE_KEY))
      .then(() => caches.keys())
      .then((cacheNames) => {
        return Promise.all(
          cacheNames.map((cacheName) => {
            // Delete any cache that starts with our prefix but isn't the current version
            // The image cache is deliberately preserved across releases: it
            // holds comic covers/pages, which are unrelated to the app
            // version. The offline cache holds user-requested downloads and
            // must likewise never be discarded by an upgrade. The state cache
            // holds the cache name records (see recordCacheName).
            if (cacheName.startsWith(CACHE_PREFIX) &&
                cacheName !== CACHE_NAME &&
                cacheName !== STATE_CACHE_NAME &&
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

  // Server-sent event streams (the page's real-time /api/events/stream
  // connection) are likewise left to the browser. A proxied stream stays open
  // for as long as the page does, and a worker with a response still in flight
  // is never idle, so the browser would not let a waiting update take over:
  // the "new version" banner's Reload button would sit on "Reloading…". A live
  // stream has nothing to cache or replay anyway.
  if (request.headers.get('Accept')?.includes('text/event-stream')) {
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
      activeCacheName().then((cacheName) => caches.open(cacheName).then((cache) =>
        cache.match(request).then((cachedResponse) => {
          const networkFetch = fetch(request)
            .then((networkResponse) => {
              if (networkResponse && networkResponse.status === 200 && networkResponse.type === 'basic') {
                cache.put(request, networkResponse.clone())
                  .then(() => trimCache(cacheName, MAX_RUNTIME_CACHE_ENTRIES))
                  .catch((error) => {
                    console.log('Service Worker: Failed to cache asset', url.pathname, error);
                  });
              }
              return networkResponse;
            });
          if (cachedResponse) {
            // Serve the cached copy at once; the fetch above refreshes it in
            // the background, and failing to (offline) changes nothing.
            networkFetch.catch(() => { /* offline; keep the cached copy */ });
            return cachedResponse;
          }
          // Nothing cached: wait for the network, and fail as it does offline.
          return networkFetch;
        })
      ))
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
            activeCacheName().then((cacheName) => caches.open(cacheName).then((cache) =>
              cache.put(request, responseToCache)
                .then(() => trimCache(cacheName, MAX_RUNTIME_CACHE_ENTRIES))
            )).catch((error) => {
              console.log('Service Worker: Failed to cache asset', url.pathname, error);
            });
          }
          
          return response;
        });
      })
      .catch(() => new Response('Offline', { status: 503 }))
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
