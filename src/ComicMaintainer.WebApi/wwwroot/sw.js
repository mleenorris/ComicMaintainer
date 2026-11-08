// Service Worker for Comic Maintainer PWA
// Provides basic offline support and caching
// Version is dynamically injected by the server to force browser updates

// Cache version will be determined dynamically from the API. Install and
// activate also record the name (see STATE_CACHE_NAME below): the browser
// stops an idle worker and evaluates this script afresh for its next event,
// and that new instance must keep using the same cache, not this default.
let CACHE_NAME = 'comic-maintainer-v2'; // Default fallback
const CACHE_PREFIX = 'comic-maintainer-';
// The shell every release precaches; install fails if any of it cannot be
// stored. /reader.html is the document that renders comics downloaded for
// offline reading (offline.html links to it). It must be precached rather than
// left to the runtime document cache: that copy lives in the versioned cache,
// which activate deletes on every upgrade, so after an update a saved comic
// could otherwise not be opened offline until the reader had been visited
// online again.
const urlsToCache = [
  '/manifest.json',
  '/offline.html',
  '/reader.html',
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
  '/js/app/suwayomi-search.js',
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

// Stylesheets, linked with the same ?v=<version> as the scripts (index.html
// and reader.html both use main.css).
const STYLESHEETS = [
  '/css/main.css'
];

// The versioned assets the offline reader cannot work without: what
// reader.html and offline.html load. Unlike the rest of the app scripts these
// are required, like urlsToCache. A release that activated without them would
// leave every downloaded comic unreadable offline, so its install fails
// instead and the previous release, whose cache still holds them, stays in
// control until the next update check retries.
const OFFLINE_READER_ASSETS = [
  '/css/main.css',
  '/js/shared.js',
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

// How many pages of one comic are fetched at once while downloading it. A few
// requests in flight hide the per-request latency that dominates on a slow or
// distant connection without hogging the bandwidth the reader itself needs.
const OFFLINE_DOWNLOAD_CONCURRENCY = 3;

// A download the worker could not finish (the browser stops a worker whose
// event runs for minutes, the app was closed, the connection dropped) keeps
// the pages it already stored, so saving the comic again resumes where it
// stopped instead of starting over. Until it finishes it is never reported as
// saved, and one nobody resumed for this long is assumed abandoned and removed.
const INCOMPLETE_DOWNLOAD_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;

// Downloads running in this worker, by file path: the pages to report progress
// to, how far it got and the promise that settles when it ends. A worker that
// was stopped mid-download starts again with none, which is how an interrupted
// download is told apart from one that is still running.
const activeOfflineDownloads = new Map();

function errorText(error) {
  return String(error && error.message ? error.message : error);
}

function jsonResponse(value) {
  return new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });
}

async function readCachedJson(cache, key) {
  try {
    const response = await cache.match(key);
    return response ? await response.json() : null;
  } catch (error) {
    return null;
  }
}

async function offlineCacheUrls(cache) {
  return new Set((await cache.keys()).map((request) => request.url));
}

// The page numbers in 1..totalPages that are not stored for filePath; `urls`
// is the set of URLs in the offline cache.
function missingOfflinePages(urls, filePath, totalPages) {
  const missing = [];
  for (let page = 1; page <= totalPages; page++) {
    if (!urls.has(offlineCacheKey(filePath, 'page', page))) {
      missing.push(page);
    }
  }
  return missing;
}

// Run `task` for every item, at most `limit` at a time. Stops starting new
// items after the first failure, but only rejects once the ones already
// running have settled, so nothing is still being stored when the caller
// cleans up. Of several failures an interrupted one is the one reported: the
// caller keeps what was stored then, and a resume meets any real refusal again.
async function forEachConcurrently(items, limit, task) {
  let next = 0;
  let failed = false;
  const lane = async () => {
    while (!failed && next < items.length) {
      const item = items[next++];
      try {
        await task(item);
      } catch (error) {
        failed = true;
        throw error;
      }
    }
  };
  const lanes = [];
  for (let i = 0; i < Math.min(limit, items.length); i++) {
    lanes.push(lane());
  }
  const results = await Promise.allSettled(lanes);
  const failures = results.filter((result) => result.status === 'rejected').map((result) => result.reason);
  if (failures.length > 0) {
    throw failures.find((failure) => failure && failure.interrupted) || failures[0];
  }
}

// Download every page of a comic (plus its info payload, which the reader
// needs before it can render anything) into the offline cache, reporting
// progress to every page that asked for it. Asking again while the comic is
// downloading joins that download; asking for a comic an earlier attempt left
// incomplete fetches only the pages it is missing. The comic only counts as
// saved once its completion record is written, after the last page.
function downloadComicForOffline(client, options) {
  const filePath = options && options.filePath;
  if (!filePath) {
    return Promise.resolve();
  }

  const canReply = Boolean(client) && typeof client.postMessage === 'function';
  let download = activeOfflineDownloads.get(filePath);
  if (!download) {
    download = { filePath, clients: new Set(), completed: 0, total: 0 };
    download.promise = runOfflineDownload(download, (options && options.headers) || {})
      .finally(() => activeOfflineDownloads.delete(filePath));
    activeOfflineDownloads.set(filePath, download);
  } else if (canReply && download.total) {
    // A page joining a running download learns how far it already got.
    client.postMessage({ filePath, type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: download.completed, total: download.total });
  }
  if (canReply) {
    download.clients.add(client);
  }
  return download.promise;
}

async function runOfflineDownload(download, headers) {
  const filePath = download.filePath;
  const post = (message) => {
    for (const client of download.clients) {
      client.postMessage(Object.assign({ filePath }, message));
    }
  };

  const fetchOptions = { credentials: 'include', headers };
  let cache;

  const fetchAndStore = async (requestUrl, key) => {
    let response;
    let body;
    try {
      response = await fetch(requestUrl, fetchOptions);
      // fetch() resolves once the headers are in, so most of a page's transfer
      // is still to come. Reading the body here is what makes a connection
      // lost during it count as one: left to cache.put() it would look like a
      // storage failure.
      if (response.ok) {
        body = await response.blob();
      }
    } catch (error) {
      // The connection went away: what is stored so far is kept for a resume.
      throw Object.assign(new Error(errorText(error)), { interrupted: true });
    }
    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}`);
    }
    const complete = new Response(body, {
      status: response.status,
      statusText: response.statusText,
      headers: response.headers
    });
    await cache.put(key, complete.clone());
    return complete;
  };

  try {
    cache = await caches.open(OFFLINE_CACHE_NAME);

    // Until this attempt finishes, whatever is stored for the comic belongs to
    // an unfinished download; the timestamp lets an abandoned one expire.
    await cache.delete(offlineCacheKey(filePath, 'complete'));
    await cache.put(offlineCacheKey(filePath, 'pending'), jsonResponse({ updatedAt: Date.now() }));

    const previousInfo = await readCachedJson(cache, offlineCacheKey(filePath, 'info'));
    if (previousInfo) {
      download.total = Number(previousInfo.pageCount) || 0;
      download.completed = download.total
        - missingOfflinePages(await offlineCacheUrls(cache), filePath, download.total).length;
    }

    const infoResponse = await fetchAndStore(
      `/api/ComicReader/info?filePath=${encodeURIComponent(filePath)}`,
      offlineCacheKey(filePath, 'info')
    );
    const info = await infoResponse.json();
    const totalPages = Number(info.pageCount) || 0;
    if (totalPages < 1) {
      throw new Error('Comic reports no pages');
    }

    // Pages kept from an earlier attempt belong to another version of the
    // file when its page count changed since; they cannot be mixed in.
    if (previousInfo && Number(previousInfo.pageCount) !== totalPages) {
      await removeOfflineEntries(cache, filePath, 'page/');
    }

    const missing = missingOfflinePages(await offlineCacheUrls(cache), filePath, totalPages);
    download.total = totalPages;
    download.completed = totalPages - missing.length;
    post({ type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: download.completed, total: totalPages });

    await forEachConcurrently(missing, OFFLINE_DOWNLOAD_CONCURRENCY, async (page) => {
      await fetchAndStore(
        `/api/ComicReader/page?filePath=${encodeURIComponent(filePath)}&page=${page}`,
        offlineCacheKey(filePath, 'page', page)
      );
      download.completed++;
      post({ type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: download.completed, total: totalPages });
    });

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

    await cache.put(offlineCacheKey(filePath, 'complete'), jsonResponse({ pageCount: totalPages }));
    await cache.delete(offlineCacheKey(filePath, 'pending'));
    post({ type: 'OFFLINE_DOWNLOAD_COMPLETE', total: totalPages, fileName: info.fileName });
  } catch (error) {
    if (error && error.interrupted && download.completed > 0) {
      post({
        type: 'OFFLINE_DOWNLOAD_FAILED',
        error: errorText(error),
        resumable: true,
        completed: download.completed,
        total: download.total
      });
      return;
    }
    // Nothing worth resuming (the server refused the comic or a page, or
    // storage ran out): roll the download back rather than keep a fragment.
    await removeOfflineComic(filePath);
    post({ type: 'OFFLINE_DOWNLOAD_FAILED', error: errorText(error) });
  }
}

// Delete the offline entries stored for filePath whose kind ('info', 'cover',
// 'page/N', ...) starts with `kindPrefix`; all of them by default.
async function removeOfflineEntries(cache, filePath, kindPrefix = '') {
  const prefix = `${self.location.origin}${OFFLINE_KEY_PREFIX}${encodeURIComponent(filePath)}/${kindPrefix}`;
  const keys = await cache.keys();
  await Promise.all(
    keys
      .filter((request) => request.url.startsWith(prefix))
      .map((request) => cache.delete(request))
  );
}

async function removeOfflineComic(filePath) {
  await removeOfflineEntries(await caches.open(OFFLINE_CACHE_NAME), filePath);
}

// Summarise the offline cache for a client (the reader button, the offline
// page). `comics` lists the comics that finished downloading, with the number
// of pages saved; `incomplete` lists the ones still downloading (`active`) or
// left unfinished by an interrupted download, which saving again resumes.
// Interrupted downloads that cannot or will not be resumed are removed.
async function listOfflineComics() {
  const cache = await caches.open(OFFLINE_CACHE_NAME);
  const keys = await cache.keys();
  const urls = new Set(keys.map((request) => request.url));
  const prefix = `${self.location.origin}${OFFLINE_KEY_PREFIX}`;
  const stored = new Map();

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
    const entry = stored.get(filePath) || { filePath, pageCount: 0, complete: false };
    const kind = remainder.slice(separator + 1);
    if (kind.startsWith('page/')) {
      entry.pageCount++;
    } else if (kind === 'complete') {
      entry.complete = true;
    }
    stored.set(filePath, entry);
  }

  const comics = [];
  const incomplete = [];
  for (const entry of stored.values()) {
    const { filePath } = entry;
    const running = activeOfflineDownloads.get(filePath);
    if (running) {
      incomplete.push({ filePath, savedPages: running.completed, pageCount: running.total, active: true });
      continue;
    }

    if (entry.complete) {
      comics.push({ filePath, pageCount: entry.pageCount });
      continue;
    }

    const info = await readCachedJson(cache, offlineCacheKey(filePath, 'info'));
    const totalPages = (info && Number(info.pageCount)) || 0;
    const pending = await readCachedJson(cache, offlineCacheKey(filePath, 'pending'));
    if (!pending) {
      // Downloads made before completion was recorded have neither marker.
      // Those that got every page their info lists are complete: record it,
      // so they are listed straight away from now on.
      if (totalPages > 0 && missingOfflinePages(urls, filePath, totalPages).length === 0) {
        await cache.put(offlineCacheKey(filePath, 'complete'), jsonResponse({ pageCount: totalPages }));
        comics.push({ filePath, pageCount: entry.pageCount });
      } else {
        await removeOfflineEntries(cache, filePath);
      }
      continue;
    }

    const age = Date.now() - (Number(pending.updatedAt) || 0);
    if (totalPages > 0 && entry.pageCount > 0 && age < INCOMPLETE_DOWNLOAD_MAX_AGE_MS) {
      incomplete.push({ filePath, savedPages: entry.pageCount, pageCount: totalPages, active: false });
    } else {
      await removeOfflineEntries(cache, filePath);
    }
  }

  return { comics, incomplete };
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
// no-longer-authorized content. That work outlasts the response, so it is
// passed to the fetch `event`'s waitUntil(): the browser may stop the worker
// once the response is delivered, cutting off anything not passed to it.
async function serveImageFromCache(request, event) {
  const cache = await caches.open(IMAGE_CACHE_NAME);
  const cacheKey = cacheKeyForRequest(request);
  const cached = await cache.match(cacheKey);

  const networkFetch = fetch(request).then((response) => {
    if (response && response.status === 200) {
      // Only successful, non-opaque responses are worth storing.
      event.waitUntil(
        cache.put(cacheKey, response.clone())
          .then(() => trimCache(IMAGE_CACHE_NAME, MAX_IMAGE_CACHE_ENTRIES))
          .catch(() => { /* quota errors are non-fatal */ })
      );
    } else if (response && (response.status === 401 || response.status === 403)) {
      // No longer authorized for this entry; stop serving it from cache.
      event.waitUntil(cache.delete(cacheKey).catch(() => { /* best effort */ }));
    }
    return response;
  });

  if (cached) {
    // Serve the cached copy immediately; let the revalidation above update
    // or evict the entry in the background. A network failure here just
    // means we keep serving the (still the best available) cached copy.
    event.waitUntil(networkFetch.catch(() => { /* offline; keep serving the cached entry */ }));
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

function versionedAssetUrl(asset) {
  return `${asset}?v=${APP_VERSION}`;
}

// Every script and stylesheet install precaches under its versioned URL.
function versionedAssets() {
  return [...APP_SCRIPTS, ...PAGE_SCRIPTS, ...STYLESHEETS];
}

// An update that cannot learn its version cannot precache what the pages will
// request, and activating it would delete the cache of the release in control,
// the only one holding the offline reader's assets. So its install fails, and
// the next update check tries again. A first install has nothing to lose and
// carries on with the default cache.
function refuseUpdateWithoutVersion() {
  if (!APP_VERSION && self.registration.active) {
    throw new Error('Cannot install an update without knowing its version');
  }
}

// Precache the versioned scripts and stylesheets. Without a known version (a
// first install only, see above) the URLs the pages will request cannot be
// predicted, so nothing is. Otherwise the offline reader's assets must all be
// stored (the returned promise rejects, failing the install, if one cannot
// be); any other asset that fails is logged and fetched on demand later
// instead.
function precacheVersionedAssets(cache) {
  if (!APP_VERSION) {
    return Promise.resolve();
  }
  return Promise.all(versionedAssets().map((asset) =>
    cache.add(versionedAssetUrl(asset)).catch((error) => {
      console.log('Service Worker: Failed to precache', asset, error);
      if (OFFLINE_READER_ASSETS.includes(asset)) {
        throw error;
      }
    })
  ));
}

// The absolute URLs of everything install puts in the versioned cache. A
// woken-up worker knows them too: recallCacheName restores APP_VERSION.
function precachedUrls() {
  const assets = APP_VERSION ? versionedAssets().map(versionedAssetUrl) : [];
  return new Set([...urlsToCache, ...assets].map((path) => new URL(path, self.location.origin).href));
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
      .then(refuseUpdateWithoutVersion)
      .then(() => caches.open(CACHE_NAME))
      .then((cache) => {
        console.log('Service Worker: Caching essential files with cache name:', CACHE_NAME);
        return cache.addAll(urlsToCache).then(() => precacheVersionedAssets(cache));
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
            return serveImageFromCache(request, event).catch(
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
      serveImageFromCache(request, event).catch(
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
                event.waitUntil(
                  cache.put(request, networkResponse.clone())
                    .then(() => trimCache(cacheName, MAX_RUNTIME_CACHE_ENTRIES))
                    .catch((error) => {
                      console.log('Service Worker: Failed to cache asset', url.pathname, error);
                    })
                );
              }
              return networkResponse;
            });
          if (cachedResponse) {
            // Serve the cached copy at once; the fetch above refreshes it in
            // the background (waitUntil keeps the worker running until it is
            // stored), and failing to (offline) changes nothing.
            event.waitUntil(networkFetch.catch(() => { /* offline; keep the cached copy */ }));
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
          
          // Cache static assets (waitUntil: the worker may otherwise be
          // stopped once the response is delivered, before they are stored)
          if (url.pathname.startsWith('/icons/')) {
            event.waitUntil(
              activeCacheName().then((cacheName) => caches.open(cacheName).then((cache) =>
                cache.put(request, responseToCache)
                  .then(() => trimCache(cacheName, MAX_RUNTIME_CACHE_ENTRIES))
              )).catch((error) => {
                console.log('Service Worker: Failed to cache asset', url.pathname, error);
              })
            );
          }
          
          return response;
        });
      })
      .catch(() => new Response('Offline', { status: 503 }))
  );
});

// Listen for messages from the client
self.addEventListener('message', (event) => {
  // Only pages of this origin can reach the worker; never act on anything else.
  if (event.origin !== self.location.origin) {
    return;
  }

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
      listOfflineComics().then(({ comics, incomplete }) => replyToMessage(event, {
        type: 'OFFLINE_COMICS',
        comics,
        incomplete
      }))
    );
  }
});
