'use strict';

// Offline downloads in sw.js: DOWNLOAD_COMIC stores a comic's info, pages and
// cover under canonical keys while reporting progress to the page, and
// LIST_OFFLINE_COMICS / REMOVE_OFFLINE_COMIC let the pages show and delete
// what was saved.

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    IMAGE_CACHE,
    OFFLINE_CACHE,
    reply,
    image,
    installedWorker,
    createClient,
    createPort,
    routeComic,
    offlineKey
} = require('./support/sw-helpers');

const download = (sw, filePath, { headers, source = createClient() } = {}) =>
    sw.message({ type: 'DOWNLOAD_COMIC', filePath, ...(headers ? { headers } : {}) }, { source }).then(() => source);

test('DOWNLOAD_COMIC saves the info, every page and the cover with the page\'s credentials, reporting progress', async () => {
    const sw = await installedWorker();
    const filePath = 'Series/Vol 1 #1.cbz';
    routeComic(sw.network, filePath, { pages: 3 });
    const headers = { Authorization: 'Bearer token-123', 'X-Client-Session': 'session-a' };

    const client = await download(sw, filePath, { headers });

    assert.deepEqual(client.messages, [
        { filePath, type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 0, total: 3 },
        { filePath, type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 1, total: 3 },
        { filePath, type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 2, total: 3 },
        { filePath, type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 3, total: 3 },
        { filePath, type: 'OFFLINE_DOWNLOAD_COMPLETE', total: 3, fileName: 'Vol 1 #1.cbz' }
    ]);
    const file = encodeURIComponent(filePath);
    assert.deepEqual(sw.network.calls.map(call => call.path + call.search), [
        `/api/ComicReader/info?filePath=${file}`,
        `/api/ComicReader/page?filePath=${file}&page=1`,
        `/api/ComicReader/page?filePath=${file}&page=2`,
        `/api/ComicReader/page?filePath=${file}&page=3`,
        `/api/ComicReader/cover?filePath=${file}`
    ]);
    for (const call of sw.network.calls) {
        assert.equal(call.init.credentials, 'include');
        assert.equal(call.headers.authorization, 'Bearer token-123');
        assert.equal(call.headers['x-client-session'], 'session-a');
    }
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [
        offlineKey(filePath, 'info'),
        offlineKey(filePath, 'page/1'),
        offlineKey(filePath, 'page/2'),
        offlineKey(filePath, 'page/3'),
        offlineKey(filePath, 'cover')
    ]);
    assert.equal(await sw.caches.text(OFFLINE_CACHE, offlineKey(filePath, 'page/2')), 'Vol 1 #1.cbz page 2');
});

test('a downloaded comic reads offline, even after sign-out cleared the image cache', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 2 });
    await download(sw, 'a.cbz');
    await sw.message({ type: 'CLEAR_IMAGE_CACHE' });
    sw.network.offline = true;

    assert.deepEqual(await (await sw.respond('/api/ComicReader/info?filePath=a.cbz')).json(), { filePath: 'a.cbz', fileName: 'a.cbz', pageCount: 2 });
    assert.equal(await (await sw.respond('/api/ComicReader/page?filePath=a.cbz&page=2')).text(), 'a.cbz page 2');
    assert.equal(await (await sw.respond('/api/ComicReader/cover?filePath=a.cbz')).text(), 'a.cbz cover');
    assert.equal(sw.caches.urls(IMAGE_CACHE).length, 0);
});

test('a download without headers still sends credentials', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 1 });

    const client = await download(sw, 'a.cbz');

    assert.equal(client.messages.at(-1).type, 'OFFLINE_DOWNLOAD_COMPLETE');
    assert.ok(sw.network.calls.every(call => call.init.credentials === 'include' && !('authorization' in call.headers)));
});

test('a download that fails part-way is rolled back and reported, leaving other downloads alone', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(OFFLINE_CACHE, { [offlineKey('a.cbz.bak', 'page/1')]: image('other comic') });
    routeComic(sw.network, 'a.cbz', { pages: 3 });
    sw.network.route(call => call.path === '/api/ComicReader/page' && call.query.page === '2', reply(500, { error: 'boom' }));

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages, [
        { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 0, total: 3 },
        { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 1, total: 3 },
        { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'Request failed with status 500' }
    ]);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [offlineKey('a.cbz.bak', 'page/1')]);
    assert.equal(sw.network.callsTo('/api/ComicReader/cover').length, 0);
});

test('a comic the server cannot describe is not downloaded', async () => {
    const sw = await installedWorker();
    sw.network.route('GET /api/ComicReader/info', reply(404, { error: 'File not found' }));

    const client = await download(sw, 'gone.cbz');

    assert.deepEqual(client.messages, [{ filePath: 'gone.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'Request failed with status 404' }]);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), []);
    assert.equal(sw.network.calls.length, 1);
});

test('a comic without pages is reported as failed and nothing is kept', async () => {
    for (const pageCount of [0, undefined, 'many']) {
        const sw = await installedWorker();
        sw.network.route('GET /api/ComicReader/info', { filePath: 'empty.cbz', fileName: 'empty.cbz', pageCount });

        const client = await download(sw, 'empty.cbz');

        assert.deepEqual(client.messages, [{ filePath: 'empty.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'Comic reports no pages' }], String(pageCount));
        assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [], String(pageCount));
    }
});

test('a missing cover does not fail an otherwise complete download', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 1 });
    sw.network.route('GET /api/ComicReader/cover', reply(404, { error: 'No cover' }));

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages.at(-1), { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_COMPLETE', total: 1, fileName: 'a.cbz' });
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [offlineKey('a.cbz', 'info'), offlineKey('a.cbz', 'page/1')]);
    assert.equal(sw.logged('Failed to cache cover for offline comic').length, 1);
});

test('a storage failure is reported by name when it carries no message', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 1 });
    const cache = await sw.caches.open(OFFLINE_CACHE);
    cache.put = async () => {
        throw new DOMException('', 'QuotaExceededError');
    };

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages, [{ filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'QuotaExceededError' }]);
});

test('a download without a file path does nothing', async () => {
    const sw = await installedWorker();

    const client = await download(sw, undefined);
    await sw.context.downloadComicForOffline(client, undefined);

    assert.deepEqual(client.messages, []);
    assert.deepEqual(sw.network.calls, []);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), []);
});

test('a download requested without a way to reply still completes', async () => {
    for (const source of [null, { id: 'not-a-window' }]) {
        const sw = await installedWorker();
        routeComic(sw.network, 'a.cbz', { pages: 1 });

        await sw.message({ type: 'DOWNLOAD_COMIC', filePath: 'a.cbz' }, { source });

        assert.equal(sw.caches.urls(OFFLINE_CACHE).length, 3);
    }
});

// --- listing and removing ----------------------------------------------------

test('LIST_OFFLINE_COMICS replies with each downloaded comic and how many pages it has saved', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'Series/B.cbz', { pages: 2 });
    routeComic(sw.network, 'A 100%.cbz', { pages: 1 });
    await download(sw, 'Series/B.cbz');
    await download(sw, 'A 100%.cbz');
    const port = createPort();

    await sw.message({ type: 'LIST_OFFLINE_COMICS' }, { ports: [port] });

    assert.deepEqual(port.messages, [{
        type: 'OFFLINE_COMICS',
        comics: [
            { filePath: 'Series/B.cbz', pageCount: 2 },
            { filePath: 'A 100%.cbz', pageCount: 1 }
        ]
    }]);
});

test('entries that are not canonical download keys are left out of the list', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(OFFLINE_CACHE, {
        [offlineKey('a.cbz', 'info')]: { pageCount: 1 },
        [offlineKey('a.cbz', 'page/1')]: image('page 1'),
        '/api/ComicReader/page?filePath=b.cbz&page=1': image('stray'),
        '/__offline-comic/no-separator': image('stray'),
        '/__offline-comic/%E0%A4%A/page/1': image('undecodable')
    });
    const port = createPort();

    await sw.message({ type: 'LIST_OFFLINE_COMICS' }, { ports: [port] });

    assert.deepEqual(port.messages, [{ type: 'OFFLINE_COMICS', comics: [{ filePath: 'a.cbz', pageCount: 1 }] }]);
});

test('without a reply port the answer goes to the sending page, if any', async () => {
    const sw = await installedWorker();
    const client = createClient();

    await sw.message({ type: 'LIST_OFFLINE_COMICS' }, { source: client });
    await sw.message({ type: 'LIST_OFFLINE_COMICS' });
    await sw.message({ type: 'LIST_OFFLINE_COMICS' }, { source: { id: 'not-a-window' } });

    assert.deepEqual(client.messages, [{ type: 'OFFLINE_COMICS', comics: [] }]);
});

test('REMOVE_OFFLINE_COMIC deletes only that comic and confirms it', async () => {
    const sw = await installedWorker();
    for (const filePath of ['a.cbz', 'a.cbz.bak', 'b.cbz']) {
        routeComic(sw.network, filePath, { pages: 1 });
        await download(sw, filePath);
    }
    const port = createPort();
    const client = createClient();

    await sw.message({ type: 'REMOVE_OFFLINE_COMIC', filePath: 'a.cbz' }, { ports: [port], source: client });

    assert.deepEqual(port.messages, [{ type: 'OFFLINE_COMIC_REMOVED', filePath: 'a.cbz' }]);
    assert.deepEqual(client.messages, []);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [
        offlineKey('a.cbz.bak', 'info'), offlineKey('a.cbz.bak', 'page/1'), offlineKey('a.cbz.bak', 'cover'),
        offlineKey('b.cbz', 'info'), offlineKey('b.cbz', 'page/1'), offlineKey('b.cbz', 'cover')
    ]);

    // Removal works without anyone to tell, and the sending page is told when there is no port.
    await sw.message({ type: 'REMOVE_OFFLINE_COMIC', filePath: 'b.cbz' });
    await sw.message({ type: 'REMOVE_OFFLINE_COMIC', filePath: 'a.cbz.bak' }, { source: client });
    assert.deepEqual(client.messages, [{ type: 'OFFLINE_COMIC_REMOVED', filePath: 'a.cbz.bak' }]);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), []);
});
