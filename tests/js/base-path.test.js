'use strict';

// Serving the app under a subfolder (BASE_PATH): no URL in wwwroot is written
// relative to the domain root, and what the scripts and the service worker
// build at run time stays inside the base path.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { WWWROOT } = require('./support/sandbox');
const { loadPage, respond } = require('./support/dom');
const { bootRoutes, validToken } = require('./support/app');
const { settingsRoutes } = require('./support/settings-helpers');
const {
    APP_VERSION,
    VERSIONED_CACHE,
    IMAGE_CACHE,
    reply,
    image,
    makeRequest,
    navigationRequest,
    loadServiceWorker,
    createClient,
    routeComic
} = require('./support/sw-helpers');

const BASE = '/comics';

/** Every file under `dir`, relative to wwwroot with forward slashes. */
function filesUnder(dir) {
    return fs.readdirSync(path.join(WWWROOT, dir), { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile())
        .map(entry => path.relative(WWWROOT, path.join(entry.parentPath, entry.name)).split(path.sep).join('/'));
}

const read = file => fs.readFileSync(path.join(WWWROOT, file), 'utf8');

const HTML_ROOT_URL = /\b(?:src|href|action)\s*=\s*\\?["']\/(?!\/)|\burl=\/|location\.replace\(['"]\//;
const CSS_ROOT_URL = /url\(\s*['"]?\/(?!\/)/;
const JSON_ROOT_URL = /"(?:start_url|scope|id|src)"\s*:\s*"\//;
const SCRIPT_ROOT_URLS = [
    /\bfetch\(\s*['"`]\//,
    /\bnew EventSource\(\s*['"`]\//,
    /\.register\(\s*['"`]\//,
    /\bscope:\s*['"`]\//,
    /\blocation\.(?:href\s*=|replace\(|assign\()\s*['"`]\//,
    /\.(?:href|src)\s*=\s*['"`]\/(?!\/)/,
    /\bcaches\.match\(\s*['"`]\//
];

/** The lines of `text` a pattern matches, for a readable failure. */
function offendingLines(text, patterns) {
    return text.split(/\r?\n/).filter(line => patterns.some(pattern => pattern.test(line)));
}

test('no page links a file from the domain root', () => {
    const pages = fs.readdirSync(WWWROOT).filter(file => file.endsWith('.html'));
    assert.ok(pages.includes('index.html') && pages.includes('login.html'));
    for (const page of pages) {
        assert.deepEqual(offendingLines(read(page), [HTML_ROOT_URL]), [], page);
    }
});

test('every page says where it is served from', () => {
    for (const page of ['index.html', 'login.html', 'setup.html', 'reader.html', 'offline.html', 'jobs.html']) {
        assert.match(read(page), /<html lang="en" data-base-path="__BASE_PATH__">/, page);
    }
});

test('no script builds a URL from the domain root', () => {
    for (const script of [...filesUnder('js'), 'sw.js']) {
        const text = read(script);
        assert.deepEqual(offendingLines(text, SCRIPT_ROOT_URLS), [], script);
        assert.deepEqual(offendingLines(text, [HTML_ROOT_URL]), [], script);
    }
});

test('the stylesheet and the manifest use no root-absolute URL', () => {
    assert.deepEqual(offendingLines(read('css/main.css'), [CSS_ROOT_URL]), []);
    assert.deepEqual(offendingLines(read('manifest.json'), [JSON_ROOT_URL]), []);
});

// Prefixes `routes` keyed as "METHOD /path" with the base path.
function underBase(routes) {
    return Object.fromEntries(Object.entries(routes).map(([key, value]) => {
        const [method, route] = key.split(' ');
        return [`${method} ${BASE}${route}`, value];
    }));
}

const SERIES_ROUTES = {
    'GET /api/preferences': {},
    'GET /api/files/counts': { total: 0, processed: 0, unprocessed: 0, duplicates: 0, combinableFolders: 0 },
    'GET /api/overview': {},
    'GET /api/active-job': {},
    'GET /api/watcher/status': { running: true, enabled: true },
    'GET /api/files/series': { series: [], total_series: 0, unmarked_count: 0 },
    'GET /api/files/series/facets': { genres: [], statuses: [], series_without_genres: 0, series_without_status: 0 }
};

async function bootUnderBase() {
    const page = loadPage({
        basePath: BASE,
        url: `http://localhost${BASE}/index.html#/library`,
        localStorage: { jwt_token: validToken(), username: 'alice' },
        routes: underBase({ ...bootRoutes(), ...SERIES_ROUTES })
    });
    await page.settle();
    return page;
}

test('without a base path the app builds root-relative URLs, exactly as before', () => {
    const page = loadPage({ scripts: ['js/shared.js', 'js/app/api.js'] });

    assert.equal(page.evaluate('BASE_PATH'), '');
    assert.equal(page.evaluate("apiUrl('/api/version')"), '/api/version');
    assert.equal(page.evaluate("apiUrl('/')"), '/');
});

test('the page reads the base path the server wrote into it', () => {
    const page = loadPage({ basePath: BASE, scripts: ['js/shared.js', 'js/app/api.js'] });

    assert.equal(page.evaluate('BASE_PATH'), BASE);
    assert.equal(page.evaluate("apiUrl('/api/version')"), '/comics/api/version');
    assert.equal(page.evaluate("apiUrl('reader.html')"), '/comics/reader.html');
    assert.equal(page.document.documentElement.getAttribute('data-base-path'), BASE);
});

test('a page whose placeholder was not filled in is treated as served from the root', () => {
    const page = loadPage({
        markup: '<!doctype html><html data-base-path="__BASE_PATH__"><body></body></html>',
        scripts: ['js/shared.js']
    });

    assert.equal(page.evaluate('BASE_PATH'), '');
});

test('under a base path the app talks to its API inside it', async () => {
    const page = await bootUnderBase();

    assert.equal(page.evaluate('BASE_PATH'), BASE);
    assert.ok(page.fetchCalls.length > 3);
    for (const call of page.fetchCalls) {
        assert.ok(call.path.startsWith(`${BASE}/api/`), `${call.method} ${call.url}`);
    }
    assert.equal(page.eventSources.length, 1);
    assert.match(page.eventSources[0].url, /^\/comics\/api\/events\/stream\?/);
    assert.deepEqual(page.unmatchedRequests, []);
});

test('under a base path the Settings page shows the OPDS feed URL inside it', async () => {
    const page = loadPage({
        basePath: BASE,
        url: `http://localhost${BASE}/index.html#/settings`,
        localStorage: { jwt_token: validToken(), username: 'alice' },
        routes: underBase({ ...bootRoutes(), ...SERIES_ROUTES, ...settingsRoutes() })
    });
    await page.settle();

    assert.equal(page.$('#opdsFeedUrl').value, `http://localhost${BASE}/opds/v1.2/catalog`);
});

test('under a base path the Settings page keeps its requests inside it', async () => {
    const page = loadPage({
        basePath: BASE,
        url: `http://localhost${BASE}/index.html#/settings`,
        localStorage: { jwt_token: validToken(), username: 'alice' },
        routes: underBase({
            ...bootRoutes(),
            ...SERIES_ROUTES,
            ...settingsRoutes(),
            'PUT /api/settings': {},
            'PUT /api/settings/default-preferred-language': {},
            'GET /api/settings/filename-format': { default: '{series}' },
            'POST /api/settings/reset': { success: true },
            'POST /api/settings/cleanup-database': { success: true, removedCount: 0 },
            'GET /api/processing-history': { total: 0, history: [] },
            'GET /api/version': { version: APP_VERSION },
            'GET /api/diagnostics': {},
            'GET /api/logs/files': { files: [] },
            'GET /api/logs': { content: '' },
            'POST /api/error-reports/test': {},
            'POST /api/notifications/ntfy/test': { message: 'Test sent.' },
            'POST /api/auth/change-password': {},
            'POST /api/backups': { name: 'b.db' }
        })
    });
    await page.settle();

    for (const action of [
        'saveAllSettings({})',
        'updateDefaultPreferredLanguageFromSettings()',
        'resetFilenameFormat()',
        'confirmReset()',
        'cleanupDatabaseNow()',
        'loadProcessingHistory()',
        'openAboutModal()',
        'loadDiagnostics()',
        'loadLogFiles()',
        'sendErrorReportingTest()',
        "sendNotificationTest('ntfy')",
        'createBackup()'
    ]) {
        await page.run(action);
    }
    await page.settle();

    const paths = new Set(page.fetchCalls.map(call => call.path));
    for (const path of [
        '/api/settings',
        '/api/settings/default-preferred-language',
        '/api/settings/filename-format',
        '/api/settings/reset',
        '/api/settings/cleanup-database',
        '/api/processing-history',
        '/api/version',
        '/api/diagnostics',
        '/api/logs/files',
        '/api/logs',
        '/api/error-reports/test',
        '/api/notifications/ntfy/test',
        '/api/backups'
    ]) {
        assert.ok(paths.has(BASE + path), `${BASE}${path} was not requested`);
    }
    for (const call of page.fetchCalls) {
        assert.ok(call.path.startsWith(`${BASE}/api/`), `${call.method} ${call.url}`);
    }
    assert.deepEqual(page.unmatchedRequests, []);
});

test('under a base path the service worker registers inside it', async () => {
    const page = await bootUnderBase();

    assert.deepEqual(page.serviceWorkerRegistrations.map(item => [item.url, item.options.scope]),
        [[`${BASE}/sw.js`, `${BASE}/`]]);
});

test('under a base path a lost session goes to the login page inside it', async () => {
    const page = loadPage({
        basePath: BASE,
        scripts: ['js/shared.js', 'js/app/api.js'],
        url: `http://localhost${BASE}/index.html`,
        localStorage: { jwt_token: validToken() },
        routes: { [`GET ${BASE}/api/thing`]: 401 }
    });

    await assert.rejects(page.run("apiFetch('/api/thing')"));

    assert.equal(page.navigations.at(-1).url, `http://localhost${BASE}/login.html`);
});

test('under a base path opening a comic stays inside it', async () => {
    const page = await bootUnderBase();

    page.run("readComic('Saga/Saga 001.cbz')");

    assert.equal(page.navigations.at(-1).url, `http://localhost${BASE}/reader.html?file=Saga%2FSaga%20001.cbz`);
});

test('under a base path the sign-in page calls the API and goes home inside it', async () => {
    const page = loadPage({
        html: 'login.html',
        basePath: BASE,
        url: `http://localhost${BASE}/login.html`,
        routes: {
            [`GET ${BASE}/api/auth/setup-required`]: { setupRequired: false },
            [`GET ${BASE}/api/auth/status`]: { autheliaEnabled: true, isAuthenticated: true }
        }
    });

    await page.settle();

    assert.deepEqual(page.unmatchedRequests, []);
    assert.equal(page.navigations.at(-1).url, `http://localhost${BASE}/`);
});

test('under a base path the first-run page checks its status inside it', async () => {
    const page = loadPage({
        html: 'setup.html',
        basePath: BASE,
        url: `http://localhost${BASE}/setup.html`,
        routes: { [`GET ${BASE}/api/auth/setup-required`]: { setupRequired: false } }
    });

    await page.settle();

    assert.deepEqual(page.unmatchedRequests, []);
    assert.equal(page.navigations.at(-1).url, `http://localhost${BASE}/login.html`);
});

async function readerUnderBase() {
    const page = loadPage({
        html: 'reader.html',
        basePath: BASE,
        url: `http://localhost${BASE}/reader.html?file=Saga.cbz`,
        localStorage: { jwt_token: validToken() },
        routes: {
            [`GET ${BASE}/api/ComicReader/info`]: respond(500, { error: 'stop here' }),
            [`GET ${BASE}/api/auth/user`]: { canModifyLibrary: true }
        }
    });
    await page.settle();
    return page;
}

test('under a base path the reader loads its comic inside it', async () => {
    const page = await readerUnderBase();

    assert.equal(page.fetchCalls.find(call => call.path.endsWith('/ComicReader/info')).path, `${BASE}/api/ComicReader/info`);
    for (const call of page.fetchCalls) {
        assert.ok(call.path.startsWith(`${BASE}/api/`), call.url);
    }
});

test('under a base path Back from the reader returns to the library inside it', async () => {
    const page = await readerUnderBase();
    let backs = 0;
    page.window.history.back = () => { backs++; };
    const backFrom = referrer => {
        Object.defineProperty(page.document, 'referrer', { configurable: true, value: referrer });
        const before = page.navigations.length;
        page.run('goBack()');
        return page.navigations.slice(before).map(navigation => navigation.url);
    };

    // Straight from the library: the browser's own Back restores it best.
    assert.deepEqual(backFrom(`http://localhost${BASE}/`), []);
    assert.equal(backs, 1);

    // The domain root is some other site on this host, not the library.
    assert.deepEqual(backFrom('http://localhost/'), [`http://localhost${BASE}/`]);
    assert.equal(backs, 1);
});

test('the old jobs link redirects inside the base path', () => {
    const page = loadPage({ html: 'jobs.html', basePath: BASE, url: `http://localhost${BASE}/jobs.html` });

    assert.equal(page.navigations.at(-1).url, `http://localhost${BASE}/#/jobs`);
    assert.equal(page.$('main a').getAttribute('href'), `${BASE}/#/jobs`);
});

// ── The service worker ────────────────────────────────────────────────────

async function workerUnderBase(basePath = BASE) {
    const sw = loadServiceWorker({ basePath });
    sw.network.route(call => call.sameOrigin, reply(200, 'ok'));
    sw.network.route(`GET ${basePath}/api/version`, reply(200, { version: APP_VERSION, platform: '.NET' }));
    await sw.install();
    return sw;
}

test('under a base path the worker precaches the app inside it', async () => {
    const sw = await workerUnderBase();

    const cached = sw.caches.urls(VERSIONED_CACHE);
    assert.ok(cached.includes(`http://localhost${BASE}/offline.html`));
    assert.ok(cached.includes(`http://localhost${BASE}/js/shared.js?v=${APP_VERSION}`));
    assert.deepEqual(cached.filter(url => !url.startsWith(`http://localhost${BASE}/`)), []);
    assert.equal(sw.network.calls.filter(call => call.path === `${BASE}/api/version`).length, 1);
});

test('under a base path the worker still recognises the app\'s requests', async () => {
    const sw = await workerUnderBase();
    await sw.activate();

    // A write is left to the browser, an API read goes to the network.
    const write = await sw.fetchEvent(makeRequest(`${BASE}/api/files/rename`, { method: 'POST', body: '{}' }));
    assert.equal(write.handled, false);

    // A cover is an image for the image cache.
    sw.network.route(`GET ${BASE}/api/ComicReader/cover`, image('cover-bytes'));
    await sw.respond(`${BASE}/api/ComicReader/cover?filePath=a.cbz`);
    assert.deepEqual(sw.caches.urls(IMAGE_CACHE).length, 1);

    // A navigation falls back to the offline page once the network is gone.
    sw.network.offline = true;
    const offline = await sw.respond(navigationRequest(`${BASE}/`));
    assert.equal(await offline.text(), 'ok');
    assert.equal(offline.status, 200);
});

test('under a base path a saved comic is downloaded inside it and read back offline', async () => {
    const sw = await workerUnderBase();
    await sw.activate();
    routeComic(sw.network, 'a.cbz', { pages: 2, basePath: BASE });
    sw.network.calls.length = 0;
    const client = createClient();

    await sw.message({ type: 'DOWNLOAD_COMIC', filePath: 'a.cbz' }, { source: client });

    assert.equal(client.messages.at(-1).type, 'OFFLINE_DOWNLOAD_COMPLETE');
    assert.deepEqual(sw.network.calls.map(call => call.path + call.search), [
        `${BASE}/api/ComicReader/info?filePath=a.cbz`,
        `${BASE}/api/ComicReader/page?filePath=a.cbz&page=1`,
        `${BASE}/api/ComicReader/page?filePath=a.cbz&page=2`,
        `${BASE}/api/ComicReader/cover?filePath=a.cbz`
    ]);

    sw.network.offline = true;
    assert.deepEqual(await (await sw.respond(`${BASE}/api/ComicReader/info?filePath=a.cbz`)).json(), { filePath: 'a.cbz', fileName: 'a.cbz', pageCount: 2 });
    assert.equal(await (await sw.respond(`${BASE}/api/ComicReader/page?filePath=a.cbz&page=2`)).text(), 'a.cbz page 2');
    assert.equal(await (await sw.respond(`${BASE}/api/ComicReader/cover?filePath=a.cbz`)).text(), 'a.cbz cover');
});

test('at the domain root the worker keeps its root-relative URLs', async () => {
    const sw = await workerUnderBase('');

    const cached = sw.caches.urls(VERSIONED_CACHE);
    assert.ok(cached.includes('http://localhost/offline.html'));
    assert.ok(cached.includes(`http://localhost/js/shared.js?v=${APP_VERSION}`));
});

test('a worker URL outside the base path is left as it is', async () => {
    const sw = await workerUnderBase();

    assert.equal(sw.context.appPath(new URL('http://localhost/elsewhere/x')), '/elsewhere/x');
    assert.equal(sw.context.appPath(new URL(`http://localhost${BASE}`)), '/');
    assert.equal(sw.context.appPath(new URL(`http://localhost${BASE}/js/x.js`)), '/js/x.js');
});
