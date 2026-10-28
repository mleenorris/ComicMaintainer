'use strict';

// sw.js fetch handling: which requests it answers, from where (network or
// which cache), what it stores, and what it serves once the network is gone.

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    APP_VERSION,
    VERSIONED_CACHE,
    DEFAULT_CACHE,
    IMAGE_CACHE,
    OFFLINE_CACHE,
    OFFLINE_PAGE,
    absolute,
    reply,
    html,
    image,
    script,
    makeRequest,
    navigationRequest,
    loadServiceWorker,
    installRoutes,
    installedWorker,
    routeComic,
    offlineKey
} = require('./support/sw-helpers');

const READER_PAGE = '<!DOCTYPE html><title>Reader</title>';
const INDEX_PAGE = '<!DOCTYPE html><title>Library</title>';
const COVER = '/api/ComicReader/cover?filePath=a.cbz';

// Every URL the worker keeps in any cache.
const allCached = sw => sw.caches.names().flatMap(name => sw.caches.urls(name));

// --- the worker script, API writes and event streams -------------------------

test('the worker script itself always comes from the network, bypassing the HTTP cache', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /sw.js', script('// Service Worker Version: 2.0.352.0'));

    const response = await sw.respond('/sw.js');

    assert.equal(await response.text(), '// Service Worker Version: 2.0.352.0');
    assert.equal(sw.network.calls.length, 1);
    assert.equal(sw.network.calls[0].init.cache, 'no-store');
    assert.equal(allCached(sw).includes(absolute('/sw.js')), false);
});

test('API writes and server-sent event streams are left to the browser', async () => {
    const sw = await installedWorker();
    sw.network.offline = true;
    const requests = [
        makeRequest('/api/files/rename', { method: 'POST', body: '{}', headers: { 'Content-Type': 'application/json' } }),
        makeRequest('/api/settings', { method: 'PUT', body: '{}' }),
        makeRequest('/api/files?path=a.cbz', { method: 'DELETE' }),
        makeRequest('/api/settings', { method: 'PATCH', body: '{}' }),
        // A failed write must fail visibly, not turn into the offline page.
        makeRequest('/api/auth/login', { method: 'POST', body: '{}', headers: { Accept: 'text/html' } }),
        makeRequest('/api/events/stream', { headers: { Accept: 'text/event-stream' } })
    ];

    for (const request of requests) {
        const { handled } = await sw.fetchEvent(request);
        assert.equal(handled, false, `${request.method} ${request.url}`);
    }
    assert.deepEqual(sw.network.calls, []);
});

// --- documents ----------------------------------------------------------------

test('documents are fetched network-first and the app shell is stored by path for offline use', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /reader.html', html(READER_PAGE));
    sw.network.route('GET /', html(INDEX_PAGE));

    const response = await sw.respond(navigationRequest('/reader.html?file=Series%2Fa.cbz'));

    assert.equal(await response.text(), READER_PAGE);
    assert.equal(sw.network.calls[0].url, absolute('/reader.html?file=Series%2Fa.cbz'));
    assert.equal(sw.network.calls[0].init.cache, 'no-store');
    // Every ?file= URL renders the same document, so it is stored once, by path.
    assert.equal(await sw.caches.text(VERSIONED_CACHE, '/reader.html'), READER_PAGE);
    assert.equal(allCached(sw).some(url => url.includes('?file=')), false);

    await sw.respond(navigationRequest('/'));
    assert.equal(await sw.caches.text(VERSIONED_CACHE, '/'), INDEX_PAGE);
});

test('offline, a stored document is replayed whatever its query string', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /reader.html', html(READER_PAGE));
    await sw.respond(navigationRequest('/reader.html?file=a.cbz'));
    sw.network.offline = true;

    const response = await sw.respond(navigationRequest('/reader.html?file=b.cbz'));

    assert.equal(response.status, 200);
    assert.equal(await response.text(), READER_PAGE);
});

test('only the documents needed to read offline are stored', async () => {
    const sw = await installedWorker();
    sw.network.route(call => call.sameOrigin, call => html(`<title>${call.path}</title>`));

    for (const path of ['/', '/index.html', '/reader.html', '/offline.html', '/login.html', '/setup.html', '/series/42']) {
        await sw.respond(navigationRequest(path));
    }

    for (const path of ['/', '/index.html', '/reader.html', '/offline.html']) {
        assert.equal(await sw.caches.text(VERSIONED_CACHE, path), `<title>${path}</title>`, path);
    }
    for (const path of ['/login.html', '/setup.html', '/series/42']) {
        assert.equal(allCached(sw).includes(absolute(path)), false, path);
    }
});

test('error pages, foreign responses and non-GET navigations are passed through but not stored', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /reader.html', reply(500, '<h1>Server error</h1>', { headers: { 'Content-Type': 'text/html' } }));
    // E.g. a sign-in portal reached through a redirect.
    sw.network.route('GET /index.html', reply(200, '<h1>Sign in</h1>', { headers: { 'Content-Type': 'text/html' }, type: 'cors' }));
    sw.network.route('POST /', html('<h1>Posted</h1>'));

    assert.equal((await sw.respond(navigationRequest('/reader.html?file=a.cbz'))).status, 500);
    assert.equal(await (await sw.respond(navigationRequest('/index.html'))).text(), '<h1>Sign in</h1>');
    assert.equal(await (await sw.respond(navigationRequest('/', { method: 'POST', body: 'a=1' }))).text(), '<h1>Posted</h1>');

    assert.equal(sw.network.calls[2].method, 'POST');
    for (const path of ['/reader.html', '/index.html', '/']) {
        assert.equal(allCached(sw).includes(absolute(path)), false, path);
    }
});

test('offline, a document that was never stored falls back to the precached offline page', async () => {
    const sw = await installedWorker();
    sw.network.offline = true;

    for (const path of ['/index.html', '/series/42', '/login.html']) {
        const response = await sw.respond(navigationRequest(path));
        assert.equal(response.status, 200, path);
        assert.equal(await response.text(), OFFLINE_PAGE, path);
    }
});

test('without a precached offline page an inline offline notice is served', async () => {
    const sw = loadServiceWorker();
    sw.network.offline = true;

    const response = await sw.respond(navigationRequest('/series/42'));

    assert.equal(response.status, 200);
    assert.match(response.headers.get('Content-Type'), /^text\/html/);
    assert.match(await response.text(), /<h1>Offline<\/h1><p>Comic Maintainer is unavailable while offline\.<\/p>/);
});

test('offline, a document requested without accepting HTML gets a plain 503', async () => {
    const sw = await installedWorker();
    sw.network.offline = true;

    const response = await sw.respond('/login.html');

    assert.equal(response.status, 503);
    assert.equal(await response.text(), 'Offline');
});

test('documents are recognised by navigation mode, destination, Accept header, the root path or an .html path', async () => {
    const sw = await installedWorker();
    sw.network.route(call => call.sameOrigin, html('<title>page</title>'));
    const documents = [
        makeRequest('/series/1', { mode: 'navigate' }),
        makeRequest('/series/2', { destination: 'document' }),
        makeRequest('/series/3', { headers: { Accept: 'text/html' } }),
        makeRequest('/'),
        makeRequest('/login.html')
    ];

    for (const request of documents) {
        await sw.respond(request);
    }

    // Documents bypass the HTTP cache...
    assert.deepEqual(sw.network.calls.map(call => [call.path, call.init.cache]), [
        ['/series/1', 'no-store'],
        ['/series/2', 'no-store'],
        ['/series/3', 'no-store'],
        ['/', 'no-store'],
        ['/login.html', 'no-store']
    ]);
    // ...and the root document is stored even when a script fetched it.
    assert.equal(await sw.caches.text(VERSIONED_CACHE, '/'), '<title>page</title>');

    // Anything else is an ordinary asset request.
    sw.network.calls.length = 0;
    await sw.respond('/series/4');
    assert.equal(sw.network.calls[0].init.cache, undefined);
});

test('a document that cannot be stored is still served, and the failure is logged', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /reader.html', html(READER_PAGE));
    const cache = await sw.caches.open(VERSIONED_CACHE);
    cache.put = async () => {
        throw new DOMException('Quota exceeded', 'QuotaExceededError');
    };

    const response = await sw.respond(navigationRequest('/reader.html?file=a.cbz'));

    assert.equal(await response.text(), READER_PAGE);
    assert.deepEqual(sw.logged('Failed to cache document').map(entry => entry.args[1]), ['/reader.html']);
});

test('a failure to trim the runtime cache is logged and does not lose the stored document', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /reader.html', html(READER_PAGE));
    const cache = await sw.caches.open(VERSIONED_CACHE);
    cache.keys = async () => {
        throw new DOMException('Cache is gone', 'InvalidStateError');
    };

    const response = await sw.respond(navigationRequest('/reader.html'));

    assert.equal(await response.text(), READER_PAGE);
    assert.equal(await sw.caches.text(VERSIONED_CACHE, '/reader.html'), READER_PAGE);
    assert.deepEqual(sw.logged('Failed to trim cache').map(entry => entry.args[1]), [VERSIONED_CACHE]);
});

// --- reader API: downloaded comics -------------------------------------------

test('pages, covers and info of a downloaded comic are served from the offline cache, however the URL is spelled', async () => {
    const sw = await installedWorker();
    const filePath = 'Series/A #1.cbz';
    await sw.caches.seed(OFFLINE_CACHE, {
        [offlineKey(filePath, 'info')]: { filePath, fileName: 'A #1.cbz', pageCount: 2 },
        [offlineKey(filePath, 'cover')]: image('saved cover'),
        [offlineKey(filePath, 'page/1')]: image('saved page 1'),
        [offlineKey(filePath, 'page/2')]: image('saved page 2')
    });
    const file = encodeURIComponent(filePath);
    const expectations = [
        [`/api/ComicReader/info?filePath=${file}`, JSON.stringify({ filePath, fileName: 'A #1.cbz', pageCount: 2 })],
        [`/api/ComicReader/cover?filePath=${file}`, 'saved cover'],
        [`/api/ComicReader/page?filePath=${file}&page=2`, 'saved page 2'],
        [`/api/comicreader/PAGE?page=2&filepath=${file}`, 'saved page 2'],
        // No page number means the first page.
        [`/api/ComicReader/page?filePath=${file}`, 'saved page 1']
    ];

    for (const [url, body] of expectations) {
        const response = await sw.respond(url);
        assert.equal(response.status, 200, url);
        assert.equal(await response.text(), body, url);
    }
    // Served ahead of the network, which is not even asked.
    assert.deepEqual(sw.network.calls, []);
});

test('reader requests for comics that were not downloaded use the image cache or the network', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'b.cbz', { pages: 2 });

    const page = await sw.respond('/api/ComicReader/page?filePath=b.cbz&page=1');
    assert.equal(await page.text(), 'b.cbz page 1');
    const info = await sw.respond('/api/ComicReader/info?filePath=b.cbz');
    assert.deepEqual(await info.json(), { filePath: 'b.cbz', fileName: 'b.cbz', pageCount: 2 });
    await sw.idle();
    // The page is kept in the (bounded) image cache; info is never cached and nothing counts as downloaded.
    assert.deepEqual(sw.caches.urls(IMAGE_CACHE), [absolute('/api/ComicReader/page?filePath=b.cbz&page=1')]);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), []);

    sw.network.offline = true;
    assert.equal(await (await sw.respond('/api/ComicReader/page?filePath=b.cbz&page=1')).text(), 'b.cbz page 1');
    const missingPage = await sw.respond('/api/ComicReader/page?filePath=b.cbz&page=2');
    assert.equal(missingPage.status, 503);
    assert.equal(await missingPage.text(), 'Offline');
    const offlineInfo = await sw.respond('/api/ComicReader/info?filePath=b.cbz');
    assert.equal(offlineInfo.status, 503);
    assert.equal(offlineInfo.headers.get('Content-Type'), 'application/json');
    assert.deepEqual(await offlineInfo.json(), { error: 'Offline - API unavailable' });
});

test('an unreadable offline cache falls back to the network', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz');
    const open = sw.caches.open.bind(sw.caches);
    sw.caches.open = async name => {
        if (name === OFFLINE_CACHE) throw new DOMException('The operation is insecure.', 'SecurityError');
        return open(name);
    };

    const response = await sw.respond(COVER);

    assert.equal(await response.text(), 'a.cbz cover');
});

test('reader requests that cannot belong to a download skip the offline cache', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(OFFLINE_CACHE, { [offlineKey('a.cbz', 'page/1')]: image('saved page 1') });
    sw.network.route(/ http:\/\/localhost\/api\/ComicReader\//, call => image(`network ${call.path}${call.search}`));

    for (const url of ['/api/ComicReader/page?filePath=a.cbz&page=0', '/api/ComicReader/page?filePath=a.cbz&page=first', '/api/ComicReader/cover?id=7']) {
        assert.equal(await (await sw.respond(url)).text(), `network ${url}`, url);
    }

    // An API read that merely names a file is an ordinary API call.
    sw.network.route('GET /api/files/metadata', { title: 'A' });
    assert.deepEqual(await (await sw.respond('/api/files/metadata?filePath=a.cbz')).json(), { title: 'A' });

    // Offline they behave like any other uncached image.
    sw.network.offline = true;
    const offline = await sw.respond('/api/ComicReader/cover?id=8');
    assert.equal(offline.status, 503);
    assert.equal(await offline.text(), 'Offline');
});

test('matching a request against the downloads ignores requests that cannot be part of one', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(OFFLINE_CACHE, { [offlineKey('a.cbz', 'page/1')]: image('saved page 1') });
    const url = absolute('/api/ComicReader/page?filePath=a.cbz&page=1');

    assert.equal(await sw.context.matchOfflineComic(makeRequest(url, { method: 'POST', body: '' }), new URL(url)), null);
    const withoutFile = absolute('/api/ComicReader/page?page=1');
    assert.equal(await sw.context.matchOfflineComic(makeRequest(withoutFile), new URL(withoutFile)), null);
    assert.equal(await (await sw.context.matchOfflineComic(makeRequest(url), new URL(url))).text(), 'saved page 1');
});

// --- reader API: the image cache ---------------------------------------------

test('covers and pages are cached on first view, then served from the cache while being revalidated', async () => {
    const sw = await installedWorker();
    let version = 1;
    sw.network.route('GET /api/ComicReader/cover', () => image(`cover v${version}`));

    assert.equal(await (await sw.respond(COVER)).text(), 'cover v1');
    await sw.idle();
    assert.equal(await sw.caches.text(IMAGE_CACHE, COVER), 'cover v1');

    version = 2;
    // The cached copy is served at once...
    assert.equal(await (await sw.respond(COVER)).text(), 'cover v1');
    await sw.idle();
    // ...while the network copy refreshes the entry for next time.
    assert.equal(sw.network.callsTo('/api/ComicReader/cover').length, 2);
    assert.equal(await sw.caches.text(IMAGE_CACHE, COVER), 'cover v2');
});

for (const status of [401, 403]) {
    test(`a cached image is evicted once the server answers ${status}`, async () => {
        const sw = await installedWorker();
        await sw.caches.seed(IMAGE_CACHE, { [COVER]: image('cached cover') });
        sw.network.route('GET /api/ComicReader/cover', reply(status, { error: 'Not allowed' }));

        // This view still gets the cached copy; the revalidation drops it.
        assert.equal(await (await sw.respond(COVER)).text(), 'cached cover');
        await sw.idle();
        assert.deepEqual(sw.caches.urls(IMAGE_CACHE), []);

        assert.equal((await sw.respond(COVER)).status, status);
    });
}

test('other error answers neither replace nor evict a cached image, nor get cached', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(IMAGE_CACHE, { [COVER]: image('cached cover') });
    sw.network.route('GET /api/ComicReader/cover', reply(500, { error: 'boom' }));
    sw.network.route('GET /api/ComicReader/page', reply(404, { error: 'No such page' }));

    assert.equal(await (await sw.respond(COVER)).text(), 'cached cover');
    assert.equal((await sw.respond('/api/ComicReader/page?filePath=a.cbz&page=9')).status, 404);
    await sw.idle();

    assert.deepEqual(sw.caches.urls(IMAGE_CACHE), [absolute(COVER)]);
    assert.equal(await sw.caches.text(IMAGE_CACHE, COVER), 'cached cover');
});

test('offline, cached images are still served and missing ones get a 503', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(IMAGE_CACHE, { [COVER]: image('cached cover') });
    sw.network.offline = true;

    assert.equal(await (await sw.respond(COVER)).text(), 'cached cover');
    const missing = await sw.respond('/api/ComicReader/page?filePath=a.cbz&page=3');
    assert.equal(missing.status, 503);
    assert.equal(await missing.text(), 'Offline');
    await sw.idle();
    assert.deepEqual(sw.caches.urls(IMAGE_CACHE), [absolute(COVER)]);
});

test('images are cached per sign-in session, so the next user cannot read them from the cache', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /api/ComicReader/cover', call => image(`cover for ${call.headers['x-client-session']}`));
    const asSession = id => makeRequest(COVER, { headers: { 'X-Client-Session': id } });

    assert.equal(await (await sw.respond(asSession('alice'))).text(), 'cover for alice');
    await sw.idle();
    assert.deepEqual(sw.caches.urls(IMAGE_CACHE), [absolute(`${COVER}&__sid=alice`)]);
    // Another session misses the cache and gets an entry of its own.
    assert.equal(await (await sw.respond(asSession('bob'))).text(), 'cover for bob');
    await sw.idle();

    sw.network.offline = true;
    assert.equal(await (await sw.respond(asSession('alice'))).text(), 'cover for alice');
    assert.equal(await (await sw.respond(asSession('bob'))).text(), 'cover for bob');
    assert.equal((await sw.respond(asSession('carol'))).status, 503);
    assert.equal((await sw.respond(COVER)).status, 503);
    // The session marker only lives in the cache key; the server never sees it.
    assert.ok(sw.network.calls.every(call => !call.url.includes('__sid')));
});

test('an image that cannot be stored is still served', async () => {
    const sw = await installedWorker();
    const cache = await sw.caches.open(IMAGE_CACHE);
    cache.put = async () => {
        throw new DOMException('Quota exceeded', 'QuotaExceededError');
    };
    sw.network.route('GET /api/ComicReader/page', image('page 1'));

    assert.equal(await (await sw.respond('/api/ComicReader/page?filePath=a.cbz&page=1')).text(), 'page 1');
    await sw.idle();
    assert.deepEqual(sw.caches.urls(IMAGE_CACHE), []);
});

test('a failed eviction is ignored', async () => {
    const sw = await installedWorker();
    const cache = await sw.caches.seed(IMAGE_CACHE, { [COVER]: image('cached cover') });
    cache.delete = async () => {
        throw new DOMException('Cache is gone', 'InvalidStateError');
    };
    sw.network.route('GET /api/ComicReader/cover', reply(401, { error: 'Unauthorized' }));

    assert.equal(await (await sw.respond(COVER)).text(), 'cached cover');
    await sw.idle();
    assert.deepEqual(sw.caches.urls(IMAGE_CACHE), [absolute(COVER)]);
});

test('the image cache keeps at most 400 entries, evicting the oldest first', async () => {
    const sw = await installedWorker();
    const entries = {};
    for (let i = 0; i < 400; i++) entries[`/api/ComicReader/cover?filePath=c${i}.cbz`] = image(`cover ${i}`);
    await sw.caches.seed(IMAGE_CACHE, entries);
    sw.network.route('GET /api/ComicReader/cover', call => image(`cover of ${call.query.filePath}`));

    await sw.respond('/api/ComicReader/cover?filePath=new.cbz');
    await sw.idle();

    const urls = sw.caches.urls(IMAGE_CACHE);
    assert.equal(urls.length, 400);
    assert.equal(urls[0], absolute('/api/ComicReader/cover?filePath=c1.cbz'));
    assert.equal(urls[399], absolute('/api/ComicReader/cover?filePath=new.cbz'));
});

// --- other API reads -----------------------------------------------------------

test('other API reads go to the network uncached, with a JSON 503 when offline', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /api/files', { files: ['a.cbz'] });

    assert.deepEqual(await (await sw.respond('/api/files?page=1')).json(), { files: ['a.cbz'] });
    assert.equal(sw.network.calls[0].url, absolute('/api/files?page=1'));

    sw.network.offline = true;
    const offline = await sw.respond('/api/files?page=1');
    assert.equal(offline.status, 503);
    assert.equal(offline.headers.get('Content-Type'), 'application/json');
    assert.deepEqual(await offline.json(), { error: 'Offline - API unavailable' });
    await sw.idle();
    assert.equal(allCached(sw).some(url => url.includes('/api/files')), false);
});

// --- scripts and stylesheets ---------------------------------------------------

test('scripts are served from the cache at once and refreshed in the background', async () => {
    const sw = await installedWorker();
    const MAIN = `/js/main.js?v=${APP_VERSION}`;
    sw.network.route('GET /js/main.js', script('// main.js, hotfixed'));

    // The precached copy...
    assert.equal(await (await sw.respond(MAIN)).text(), `// ${MAIN}`);
    await sw.idle();
    // ...is replaced by the network copy for next time.
    assert.equal(sw.network.callsTo('/js/main.js').length, 1);
    assert.equal(await sw.caches.text(VERSIONED_CACHE, MAIN), '// main.js, hotfixed');

    sw.network.offline = true;
    assert.equal(await (await sw.respond(MAIN)).text(), '// main.js, hotfixed');
});

test('a precached script is served offline', async () => {
    const sw = await installedWorker();
    sw.network.offline = true;

    const response = await sw.respond(`/js/pages/reader.js?v=${APP_VERSION}`);

    assert.equal(response.status, 200);
    assert.equal(await response.text(), `// /js/pages/reader.js?v=${APP_VERSION}`);
});

test('an uncached stylesheet is fetched and kept; failed and cross-origin answers are not kept', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /css/styles.css', reply(200, 'body{}', { headers: { 'Content-Type': 'text/css' } }));
    sw.network.route('GET /js/missing.js', reply(404, 'Not found'));
    sw.network.route('https://cdn.example.com/js/lib.js', script('// lib'));

    assert.equal(await (await sw.respond('/css/styles.css?v=1')).text(), 'body{}');
    assert.equal((await sw.respond('/js/missing.js')).status, 404);
    assert.equal(await (await sw.respond('https://cdn.example.com/js/lib.js')).text(), '// lib');
    await sw.idle();

    const cached = sw.caches.urls(VERSIONED_CACHE);
    assert.ok(cached.includes(absolute('/css/styles.css?v=1')));
    assert.equal(cached.includes(absolute('/js/missing.js')), false);
    assert.equal(allCached(sw).includes('https://cdn.example.com/js/lib.js'), false);
});

test('offline, a script or stylesheet that was never cached fails with the network error', async () => {
    const sw = await installedWorker();
    sw.network.offline = true;

    for (const path of ['/js/never-cached.js', '/css/never-cached.css']) {
        await assert.rejects(sw.respond(path), { name: 'TypeError', message: 'Failed to fetch' }, path);
    }
});

test('a stylesheet, script or icon that cannot be stored is still served, and the failure is logged', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /css/styles.css', reply(200, 'body{}', { headers: { 'Content-Type': 'text/css' } }));
    sw.network.route('GET /js/main.js', script('// main.js, hotfixed'));
    const cache = await sw.caches.open(VERSIONED_CACHE);
    cache.put = async () => {
        throw new DOMException('Quota exceeded', 'QuotaExceededError');
    };

    assert.equal(await (await sw.respond('/css/styles.css')).text(), 'body{}');
    // A precached script is served at once, and refreshing it fails in the background.
    assert.equal(await (await sw.respond(`/js/main.js?v=${APP_VERSION}`)).text(), `// /js/main.js?v=${APP_VERSION}`);
    assert.equal(await (await sw.respond('/icons/badge.png')).text(), 'png');
    await sw.idle();

    assert.deepEqual(sw.logged('Failed to cache asset').map(entry => entry.args[1]).sort(), ['/css/styles.css', '/icons/badge.png', '/js/main.js']);
});

test('the runtime asset cache keeps at most 120 entries, evicting the oldest first', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);
    await sw.activate(); // versioned cache name, nothing precached
    const entries = {};
    for (let i = 0; i < 120; i++) entries[`/icons/runtime-${i}.png`] = image(`icon ${i}`);
    await sw.caches.seed(VERSIONED_CACHE, entries);
    sw.network.route('GET /css/theme.css', reply(200, 'body{}', { headers: { 'Content-Type': 'text/css' } }));

    await sw.respond('/css/theme.css');
    await sw.idle();
    await sw.respond('/icons/badge.png');
    await sw.idle();

    const urls = sw.caches.urls(VERSIONED_CACHE);
    assert.equal(urls.length, 120);
    assert.equal(urls[0], absolute('/icons/runtime-2.png'));
    assert.deepEqual(urls.slice(-2), [absolute('/css/theme.css'), absolute('/icons/badge.png')]);
});

test('the runtime entry bound never evicts what install precached, also in a woken-up worker', async () => {
    for (const label of ['the worker that installed', 'a woken-up worker']) {
        const installed = await installedWorker();
        const sw = label === 'a woken-up worker' ? installed.restart() : installed;
        const precached = sw.caches.urls(VERSIONED_CACHE);
        // 120 runtime entries, all of them newer than the precache.
        const entries = {};
        for (let i = 0; i < 120; i++) entries[`/icons/runtime-${i}.png`] = image(`icon ${i}`);
        await sw.caches.seed(VERSIONED_CACHE, entries);
        sw.network.route('GET /css/theme.css', reply(200, 'body{}', { headers: { 'Content-Type': 'text/css' } }));
        sw.network.route('GET /reader.html', html(READER_PAGE));
        sw.network.route('GET /js/main.js', script('// main.js, refreshed'));

        await sw.respond('/css/theme.css');
        await sw.respond(navigationRequest('/reader.html?file=a.cbz'));
        // Refreshing a precached script stores it anew, as the latest entry.
        await sw.respond(`/js/main.js?v=${APP_VERSION}`);
        await sw.respond('/icons/badge.png');
        await sw.idle();

        const urls = sw.caches.urls(VERSIONED_CACHE);
        assert.deepEqual(urls.filter(url => precached.includes(url)).sort(), [...precached].sort(), label);
        assert.equal(await sw.caches.text(VERSIONED_CACHE, `/js/main.js?v=${APP_VERSION}`), '// main.js, refreshed', label);
        const runtime = urls.filter(url => !precached.includes(url));
        assert.equal(runtime.length, 120, label);
        assert.equal(runtime[0], absolute('/icons/runtime-3.png'), label);
        assert.deepEqual(runtime.slice(-3), [absolute('/css/theme.css'), absolute('/reader.html'), absolute('/icons/badge.png')], label);

        // An offline launch still finds the offline page and the reader's script.
        sw.network.offline = true;
        assert.equal(await (await sw.respond(navigationRequest('/series/42'))).text(), OFFLINE_PAGE, label);
        assert.equal(await (await sw.respond(`/js/pages/reader.js?v=${APP_VERSION}`)).text(), `// /js/pages/reader.js?v=${APP_VERSION}`, label);
    }
});

// --- other static files ----------------------------------------------------------

test('icons are cached on first use, and cached files are then served without the network', async () => {
    const sw = await installedWorker();

    assert.equal((await sw.respond('/icons/badge.png')).status, 200);
    await sw.idle();
    assert.ok(sw.caches.urls(VERSIONED_CACHE).includes(absolute('/icons/badge.png')));

    sw.network.calls.length = 0;
    const again = await sw.respond('/icons/badge.png');
    assert.equal(again.status, 200);
    assert.equal(await again.text(), 'png');
    // Precached files are served the same way.
    assert.deepEqual(await (await sw.respond('/manifest.json')).json(), { name: 'Comic Maintainer', start_url: '/' });
    assert.deepEqual(sw.network.calls, []);
});

test('other static files, failed answers and foreign icons are not cached; offline misses get a 503', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /fonts/inter.woff2', reply(200, 'font', { headers: { 'Content-Type': 'font/woff2' } }));
    sw.network.route('GET /icons/missing.png', reply(404, 'Not found'));
    sw.network.route('https://cdn.example.com/icons/flag.png', image('flag'));

    assert.equal(await (await sw.respond('/fonts/inter.woff2')).text(), 'font');
    assert.equal((await sw.respond('/icons/missing.png')).status, 404);
    assert.equal(await (await sw.respond('https://cdn.example.com/icons/flag.png')).text(), 'flag');
    await sw.idle();
    for (const url of [absolute('/fonts/inter.woff2'), absolute('/icons/missing.png'), 'https://cdn.example.com/icons/flag.png']) {
        assert.equal(allCached(sw).includes(url), false, url);
    }

    sw.network.offline = true;
    const offline = await sw.respond('/fonts/inter.woff2');
    assert.equal(offline.status, 503);
    assert.equal(await offline.text(), 'Offline');
});

test('a POST outside the API is forwarded to the network, never answered from the cache', async () => {
    const sw = await installedWorker();
    sw.network.route('POST /manifest.json', reply(405, 'Method Not Allowed'));

    const response = await sw.respond(makeRequest('/manifest.json', { method: 'POST', body: '{}' }));

    assert.equal(response.status, 405);
    assert.deepEqual(sw.network.calls.map(call => call.method), ['POST']);
});

// --- a worker woken up again ------------------------------------------------------
//
// The browser stops an idle worker and evaluates sw.js afresh for its next
// event, so most requests reach a worker that saw neither install nor
// activate (worker.restart()).

const STATE_CACHE = 'comic-maintainer-sw-state-v1';
const stylesheet = reply(200, 'body{}', { headers: { 'Content-Type': 'text/css' } });

test('a woken-up worker serves the precached scripts offline', async () => {
    const sw = (await installedWorker()).restart();
    sw.network.offline = true;

    for (const path of ['/js/shared.js', '/js/pages/reader.js', '/js/main.js']) {
        const response = await sw.respond(`${path}?v=${APP_VERSION}`);
        assert.equal(response.status, 200, path);
        assert.equal(await response.text(), `// ${path}?v=${APP_VERSION}`, path);
    }
    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE, STATE_CACHE]);
});

test('a woken-up worker stores documents, scripts, stylesheets and icons in the versioned cache without asking the server', async () => {
    const sw = (await installedWorker()).restart();
    sw.network.route('GET /reader.html', html(READER_PAGE));
    sw.network.route('GET /css/styles.css', stylesheet);
    sw.network.route('GET /js/main.js', script('// main.js, hotfixed'));

    await sw.respond(navigationRequest('/reader.html?file=a.cbz'));
    await sw.respond('/css/styles.css?v=1');
    await sw.respond(`/js/main.js?v=${APP_VERSION}`);
    await sw.respond('/icons/badge.png');
    await sw.idle();

    assert.equal(await sw.caches.text(VERSIONED_CACHE, '/reader.html'), READER_PAGE);
    assert.equal(await sw.caches.text(VERSIONED_CACHE, '/css/styles.css?v=1'), 'body{}');
    assert.equal(await sw.caches.text(VERSIONED_CACHE, `/js/main.js?v=${APP_VERSION}`), '// main.js, hotfixed');
    assert.equal(await sw.caches.text(VERSIONED_CACHE, '/icons/badge.png'), 'png');
    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE, STATE_CACHE]);
    assert.equal(sw.run('CACHE_NAME'), VERSIONED_CACHE);
    assert.deepEqual(sw.network.callsTo('/api/version'), []);

    sw.network.offline = true;
    assert.equal(await (await sw.restart().respond('/css/styles.css?v=1')).text(), 'body{}');
});

test('while an update waits to take over, a woken-up worker keeps to the cache of the version in control', async () => {
    const active = await installedWorker();
    // The next release installs next to it and waits for the user to accept it.
    const update = active.restart();
    update.network.route('GET /api/version', { version: '2.0.352.0' });
    await update.install();

    const sw = active.restart();
    sw.network.route('GET /css/styles.css', stylesheet);
    await sw.respond('/css/styles.css?v=1');
    await sw.idle();
    sw.network.offline = true;
    const main = await sw.respond(`/js/main.js?v=${APP_VERSION}`);

    assert.equal(await main.text(), `// /js/main.js?v=${APP_VERSION}`);
    assert.ok(sw.caches.urls(VERSIONED_CACHE).includes(absolute('/css/styles.css?v=1')));
    assert.equal(sw.caches.urls('comic-maintainer-2-0-352-0').includes(absolute('/css/styles.css?v=1')), false);
});

test('a worker that finds no recorded cache name uses the default cache', async () => {
    const sw = loadServiceWorker();
    sw.network.route('GET /reader.html', html(READER_PAGE));

    await sw.respond(navigationRequest('/reader.html'));

    assert.equal(await sw.caches.text(DEFAULT_CACHE, '/reader.html'), READER_PAGE);
    assert.deepEqual(sw.caches.names(), [DEFAULT_CACHE]);
    assert.deepEqual(sw.network.callsTo('/api/version'), []);
});

test('a recorded cache name that cannot be read is logged, and the default cache is used', async () => {
    const sw = (await installedWorker()).restart();
    sw.caches.match = async () => {
        throw new DOMException('The operation is insecure.', 'SecurityError');
    };
    sw.network.route('GET /css/styles.css', stylesheet);

    assert.equal(await (await sw.respond('/css/styles.css')).text(), 'body{}');
    await sw.idle();

    assert.equal(sw.logged('Failed to read the recorded cache name').length, 1);
    assert.equal(await sw.caches.text(DEFAULT_CACHE, '/css/styles.css'), 'body{}');
});

// --- background cache updates ------------------------------------------------
//
// Once a fetch event has its response the browser may stop the worker, unless a
// promise passed to event.waitUntil() is still pending, cutting off any cache
// update under way. Here cache writes take an event-loop turn, as with real
// cache storage (the fake one writes at once), so an update the worker left out
// of waitUntil() would still be unfinished when sw.respondEarly() reports that
// the event ended.

function deferred() {
    let resolve;
    const promise = new Promise(res => {
        resolve = res;
    });
    return { promise, resolve };
}

/** Make every write to `cache` wait for the event loop first. */
function slowStorage(cache) {
    for (const method of ['put', 'delete']) {
        const write = cache[method].bind(cache);
        cache[method] = async (...args) => {
            await new Promise(resolve => setImmediate(resolve));
            return write(...args);
        };
    }
    return cache;
}

test('refreshing a cached script keeps the worker running until the new copy is stored', async () => {
    const sw = await installedWorker();
    const MAIN = `/js/main.js?v=${APP_VERSION}`;
    slowStorage(await sw.caches.open(VERSIONED_CACHE));
    const network = deferred();
    sw.network.route('GET /js/main.js', () => network.promise);

    const { response, extended, ended } = await sw.respondEarly(MAIN);
    assert.equal(await response.text(), `// ${MAIN}`);
    await sw.idle();
    assert.equal(extended(), true, 'the refresh is not tied to the fetch event');

    network.resolve(script('// main.js, hotfixed'));
    await ended;
    assert.equal(await sw.caches.text(VERSIONED_CACHE, MAIN), '// main.js, hotfixed');
});

test('a stylesheet, icon or cover fetched for the first time is stored, and its cache trimmed, before the worker may stop', async () => {
    const sw = await installedWorker();
    const runtime = {};
    for (let i = 0; i < 120; i++) runtime[`/icons/runtime-${i}.png`] = image(`icon ${i}`);
    slowStorage(await sw.caches.seed(VERSIONED_CACHE, runtime));
    const covers = {};
    for (let i = 0; i < 400; i++) covers[`/api/ComicReader/cover?filePath=c${i}.cbz`] = image(`cover ${i}`);
    slowStorage(await sw.caches.seed(IMAGE_CACHE, covers));
    sw.network.route('GET /css/theme.css', stylesheet);
    sw.network.route('GET /api/ComicReader/cover', image('cover'));

    for (const [url, cacheName, evicted] of [
        ['/css/theme.css', VERSIONED_CACHE, '/icons/runtime-0.png'],
        ['/icons/badge.png', VERSIONED_CACHE, '/icons/runtime-1.png'],
        [COVER, IMAGE_CACHE, '/api/ComicReader/cover?filePath=c0.cbz']
    ]) {
        const { ended } = await sw.respondEarly(url);
        await ended;
        const urls = sw.caches.urls(cacheName);
        assert.equal(urls.at(-1), absolute(url), url);
        assert.equal(urls.includes(absolute(evicted)), false, url);
    }
});

for (const [status, answer, cachedAfter] of [
    [200, image('cover v2'), 'cover v2'],
    [401, reply(401, { error: 'Unauthorized' }), undefined],
    [403, reply(403, { error: 'Forbidden' }), undefined]
]) {
    test(`revalidating a cached image keeps the worker running until the ${status} answer has ${cachedAfter ? 'replaced' : 'evicted'} it`, async () => {
        // A comic's cover, and one requested in a way no download can match.
        for (const url of [COVER, '/api/ComicReader/cover?id=7']) {
            const sw = await installedWorker();
            slowStorage(await sw.caches.seed(IMAGE_CACHE, { [url]: image('cover v1') }));
            const network = deferred();
            sw.network.route('GET /api/ComicReader/cover', () => network.promise);

            const { response, extended, ended } = await sw.respondEarly(url);
            assert.equal(await response.text(), 'cover v1', url);
            await sw.idle();
            assert.equal(extended(), true, `${url}: the revalidation is not tied to the fetch event`);

            network.resolve(answer);
            await ended;
            assert.equal(await sw.caches.text(IMAGE_CACHE, url), cachedAfter, url);
        }
    });
}
