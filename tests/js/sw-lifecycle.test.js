'use strict';

// sw.js lifecycle: install (version lookup, precache of the shell and of every
// script the offline-capable documents load), activate (old cache cleanup,
// taking control of open pages) and the control messages pages send it.

const test = require('node:test');
const assert = require('node:assert/strict');
const { htmlScripts } = require('./support/sandbox');
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
    loadServiceWorker,
    installRoutes,
    installedWorker,
    offlineKey
} = require('./support/sw-helpers');

// Every script index.html, reader.html and offline.html load, as the server
// links them (?v=<version>).
function scriptsOfflineDocumentsLoad() {
    const scripts = new Set(['index.html', 'reader.html', 'offline.html'].flatMap(page => htmlScripts(page)));
    return [...scripts].map(path => absolute(`/${path}?v=${APP_VERSION}`)).sort();
}

const isScript = url => new URL(url).pathname.startsWith('/js/');

// Where install and activate record the cache they set up, for the next
// time the browser wakes the worker up.
const STATE_CACHE = 'comic-maintainer-sw-state-v1';

test('install names its cache after the server version and precaches the shell and every page script', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);

    await sw.install();

    assert.equal(sw.network.calls[0].path, '/api/version');
    assert.equal(sw.run('CACHE_NAME'), VERSIONED_CACHE);
    assert.equal(sw.run('APP_VERSION'), APP_VERSION);
    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE, STATE_CACHE]);

    const cached = sw.caches.urls(VERSIONED_CACHE);
    const shell = cached.filter(url => !isScript(url));
    assert.deepEqual(shell.slice(0, 2), [absolute('/manifest.json'), absolute('/offline.html')]);
    assert.ok(shell.length > 2);
    assert.ok(shell.slice(2).every(url => url.startsWith(absolute('/icons/'))), shell.join('\n'));
    assert.equal(await sw.caches.text(VERSIONED_CACHE, '/offline.html'), OFFLINE_PAGE);

    // The same versioned URLs the pages request, so an offline launch finds them.
    assert.deepEqual(cached.filter(isScript).sort(), scriptsOfflineDocumentsLoad());
    assert.ok(cached.includes(absolute(`/js/pages/offline.js?v=${APP_VERSION}`)));

    // An update waits for the page to accept it instead of taking over.
    assert.equal(sw.skipWaitingCalls, 0);
    assert.equal(sw.claims, 0);
    assert.deepEqual(sw.network.unmatched, []);
});

test('a script that cannot be precached is skipped without failing the install', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);
    sw.network.route('GET /js/app/library.js', reply(404, 'Not found'));
    sw.network.route('GET /js/pages/reader.js', () => {
        throw new TypeError('Failed to fetch');
    });

    await sw.install();

    const cached = sw.caches.urls(VERSIONED_CACHE);
    assert.equal(cached.includes(absolute(`/js/app/library.js?v=${APP_VERSION}`)), false);
    assert.equal(cached.includes(absolute(`/js/pages/reader.js?v=${APP_VERSION}`)), false);
    assert.ok(cached.includes(absolute(`/js/main.js?v=${APP_VERSION}`)));
    assert.ok(cached.includes(absolute(`/js/pages/offline.js?v=${APP_VERSION}`)));
    assert.equal(cached.filter(isScript).length, scriptsOfflineDocumentsLoad().length - 2);
    assert.deepEqual(sw.logged('Failed to precache').map(entry => entry.args[1]).sort(), ['/js/app/library.js', '/js/pages/reader.js']);
});

test('without a usable server version the default cache is used and no scripts are precached', async () => {
    const answers = {
        'an error status': reply(503, { error: 'starting up' }),
        'an answer without a version': { platform: '.NET' },
        'a network error': () => {
            throw new TypeError('Failed to fetch');
        }
    };
    for (const [label, answer] of Object.entries(answers)) {
        const sw = loadServiceWorker();
        installRoutes(sw.network);
        sw.network.route('GET /api/version', answer);

        await sw.install();

        assert.equal(sw.run('CACHE_NAME'), DEFAULT_CACHE, label);
        assert.deepEqual(sw.caches.names(), [DEFAULT_CACHE, STATE_CACHE], label);
        const cached = sw.caches.urls(DEFAULT_CACHE);
        assert.ok(cached.includes(absolute('/offline.html')), label);
        // Without the version the URLs the pages will request are unknown.
        assert.deepEqual(cached.filter(isScript), [], label);
        assert.deepEqual(sw.network.calls.filter(call => call.path.startsWith('/js/')), [], label);
    }
});

test('a failed version lookup is logged', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);
    sw.network.route('GET /api/version', () => {
        throw new TypeError('Failed to fetch');
    });

    await sw.install();

    assert.equal(sw.logged('Failed to fetch version, using default cache name').length, 1);
});

test('install fails, caching nothing, when an essential file cannot be fetched', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);
    sw.network.route('GET /offline.html', reply(500, 'Internal Server Error'));

    await assert.rejects(sw.install(), TypeError);

    assert.deepEqual(sw.caches.urls(VERSIONED_CACHE), []);
    // Nor does it record its cache for a later activation.
    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE]);
    assert.deepEqual(sw.network.calls.filter(call => call.path.startsWith('/js/')), []);
});

test('activate deletes caches of older versions but keeps image, offline-download and foreign caches', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);
    await sw.caches.seed('comic-maintainer-2-0-350-0', { '/offline.html': html('old offline page') });
    await sw.caches.seed(DEFAULT_CACHE, { '/js/main.js': script('// old') });
    await sw.caches.seed(IMAGE_CACHE, { '/api/comicreader/cover?filePath=a.cbz': image('cover') });
    await sw.caches.seed(OFFLINE_CACHE, { [offlineKey('a.cbz', 'page/1')]: image('page') });
    await sw.caches.seed('another-app-v1', { '/index.html': html('not ours') });

    await sw.install();
    await sw.activate();

    assert.deepEqual(sw.caches.names(), [IMAGE_CACHE, OFFLINE_CACHE, 'another-app-v1', VERSIONED_CACHE, STATE_CACHE]);
    assert.deepEqual(sw.logged('Deleting old cache').map(entry => entry.args[1]), ['comic-maintainer-2-0-350-0', DEFAULT_CACHE]);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [offlineKey('a.cbz', 'page/1')]);
    assert.equal(sw.claims, 1);
});

test('activate takes control only after the old caches are gone', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);
    await sw.caches.seed('comic-maintainer-1-0-0-0', {});
    const order = [];
    const deleteCache = sw.caches.delete.bind(sw.caches);
    sw.caches.delete = async name => {
        order.push(`delete ${name}`);
        return deleteCache(name);
    };
    sw.context.clients.claim = async () => {
        order.push('claim');
    };

    await sw.activate();

    assert.deepEqual(order, ['delete comic-maintainer-1-0-0-0', 'claim']);
});

// The browser stops a waiting worker while it is idle, so an update usually
// activates in a freshly evaluated worker (worker.restart()) that never saw
// its own install.

test('an update woken up to take over keeps the cache its install filled, whatever the server answers then', async () => {
    const conditions = {
        offline: network => {
            network.offline = true;
        },
        'with the version lookup failing': network => network.route('GET /api/version', reply(503, { error: 'starting up' })),
        'after the server moved on to a newer release': network => network.route('GET /api/version', { version: '2.0.352.0' })
    };
    for (const [label, setUp] of Object.entries(conditions)) {
        const sw = loadServiceWorker();
        installRoutes(sw.network);
        await sw.caches.seed('comic-maintainer-2-0-350-0', { '/offline.html': html('old offline page') });
        await sw.install();
        const precached = sw.caches.urls(VERSIONED_CACHE);

        const woken = sw.restart();
        setUp(woken.network);
        await woken.message({ type: 'SKIP_WAITING' });
        await woken.activate();

        assert.deepEqual(woken.caches.urls(VERSIONED_CACHE), precached, label);
        assert.deepEqual(woken.caches.names(), [VERSIONED_CACHE, STATE_CACHE], label);
        assert.deepEqual(woken.logged('Deleting old cache').map(entry => entry.args[1]), ['comic-maintainer-2-0-350-0'], label);
        assert.equal(woken.claims, 1, label);

        // Requests are answered from it from then on, also by a worker woken up later.
        woken.network.offline = true;
        const reader = await woken.restart().respond(`/js/pages/reader.js?v=${APP_VERSION}`);
        assert.equal(await reader.text(), `// /js/pages/reader.js?v=${APP_VERSION}`, label);
    }
});

test('an upgrade replaces the previous release\'s cache when every step runs in a newly woken-up worker', async () => {
    const previous = loadServiceWorker();
    installRoutes(previous.network, { version: '2.0.350.0' });
    await previous.install();
    await previous.activate();

    // The server is upgraded and the browser installs the new release next to the old one...
    const update = previous.restart();
    update.network.route('GET /api/version', { version: APP_VERSION });
    await update.install();
    assert.deepEqual(update.caches.names(), ['comic-maintainer-2-0-350-0', STATE_CACHE, VERSIONED_CACHE]);

    // ...which waits, is stopped while idle and is woken up when the user accepts the update.
    const woken = update.restart();
    await woken.message({ type: 'SKIP_WAITING' });
    await woken.activate();

    assert.deepEqual(woken.caches.names(), [STATE_CACHE, VERSIONED_CACHE]);
    assert.deepEqual(woken.logged('Deleting old cache').map(entry => entry.args[1]), ['comic-maintainer-2-0-350-0']);
    assert.equal(woken.claims, 1);
    const sw = woken.restart();
    sw.network.offline = true;
    assert.equal(await (await sw.respond(`/js/main.js?v=${APP_VERSION}`)).text(), `// /js/main.js?v=${APP_VERSION}`);
});

test('after an upgrade activated in a woken-up worker, trimming the runtime entries keeps the new release\'s precache', async () => {
    const previous = loadServiceWorker();
    installRoutes(previous.network, { version: '2.0.350.0' });
    await previous.install();
    await previous.activate();
    const update = previous.restart();
    update.network.route('GET /api/version', { version: APP_VERSION });
    await update.install();
    const precached = update.caches.urls(VERSIONED_CACHE);
    await update.restart().activate();

    const sw = update.restart();
    const entries = {};
    for (let i = 0; i < 121; i++) entries[`/icons/runtime-${i}.png`] = image(`icon ${i}`);
    await sw.caches.seed(VERSIONED_CACHE, entries);
    sw.network.route('GET /css/theme.css', reply(200, 'body{}', { headers: { 'Content-Type': 'text/css' } }));
    await sw.respond('/css/theme.css');
    await sw.idle();

    const urls = sw.caches.urls(VERSIONED_CACHE);
    assert.deepEqual(urls.slice(0, precached.length), precached);
    assert.equal(urls.length, precached.length + 120);
    assert.equal(urls[precached.length], absolute('/icons/runtime-2.png'));
    assert.equal(urls.at(-1), absolute('/css/theme.css'));
});

test('a later release that fails to install does not make the waiting update discard its cache', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);
    await sw.install();
    const precached = sw.caches.urls(VERSIONED_CACHE);

    const failed = sw.restart();
    failed.network.route('GET /api/version', { version: '2.0.352.0' });
    failed.network.route('GET /offline.html', reply(500, 'Internal Server Error'));
    await assert.rejects(failed.install(), TypeError);

    const woken = sw.restart();
    await woken.activate();

    assert.deepEqual(woken.caches.urls(VERSIONED_CACHE), precached);
    // The failed release's empty cache is stale.
    assert.deepEqual(woken.caches.names(), [VERSIONED_CACHE, STATE_CACHE]);
});

test('a cache name that cannot be recorded is logged, and activation still cleans up and takes control', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);
    await sw.caches.seed('comic-maintainer-2-0-350-0', {});
    const open = sw.caches.open.bind(sw.caches);
    sw.caches.open = async name => {
        if (name === STATE_CACHE) throw new DOMException('Quota exceeded', 'QuotaExceededError');
        return open(name);
    };

    await sw.install();
    await sw.activate();

    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE]);
    assert.deepEqual(sw.logged('Failed to record the cache name').map(entry => entry.args[1]), [VERSIONED_CACHE, VERSIONED_CACHE]);
    assert.equal(sw.claims, 1);
});

test('SKIP_WAITING lets an updated worker take over when the page accepts the update', async () => {
    const sw = await installedWorker();

    await sw.message({ type: 'SKIP_WAITING' });

    assert.equal(sw.skipWaitingCalls, 1);
});

test('CLEAR_IMAGE_CACHE (sign-out) drops cached covers and pages but keeps offline downloads', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(IMAGE_CACHE, { '/api/comicreader/cover?filePath=a.cbz': image('cover') });
    await sw.caches.seed(OFFLINE_CACHE, { [offlineKey('a.cbz', 'page/1')]: image('page') });

    await sw.message({ type: 'CLEAR_IMAGE_CACHE' });

    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE, STATE_CACHE, OFFLINE_CACHE]);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [offlineKey('a.cbz', 'page/1')]);
});

test('messages without data or of an unknown type are ignored', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(IMAGE_CACHE, { '/api/comicreader/cover?filePath=a.cbz': image('cover') });
    const port = { messages: [], postMessage(message) { this.messages.push(message); } };

    for (const data of [null, undefined, '', 'SKIP_WAITING', { type: 'RELOAD_EVERYTHING' }, { kind: 'SKIP_WAITING' }]) {
        await sw.message(data, { ports: [port] });
    }

    assert.equal(sw.skipWaitingCalls, 0);
    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE, STATE_CACHE, IMAGE_CACHE]);
    assert.deepEqual(port.messages, []);
    assert.deepEqual(sw.network.calls, []);
});
