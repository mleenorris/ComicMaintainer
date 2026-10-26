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

test('install names its cache after the server version and precaches the shell and every page script', async () => {
    const sw = loadServiceWorker();
    installRoutes(sw.network);

    await sw.install();

    assert.equal(sw.network.calls[0].path, '/api/version');
    assert.equal(sw.run('CACHE_NAME'), VERSIONED_CACHE);
    assert.equal(sw.run('APP_VERSION'), APP_VERSION);
    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE]);

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
        assert.deepEqual(sw.caches.names(), [DEFAULT_CACHE], label);
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

    assert.deepEqual(sw.caches.names(), [IMAGE_CACHE, OFFLINE_CACHE, 'another-app-v1', VERSIONED_CACHE]);
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

    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE, OFFLINE_CACHE]);
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
    assert.deepEqual(sw.caches.names(), [VERSIONED_CACHE, IMAGE_CACHE]);
    assert.deepEqual(port.messages, []);
    assert.deepEqual(sw.network.calls, []);
});
