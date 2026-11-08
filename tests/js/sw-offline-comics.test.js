'use strict';

// Offline downloads in sw.js: DOWNLOAD_COMIC stores a comic's info, pages and
// cover under canonical keys while reporting progress to the page, and
// LIST_OFFLINE_COMICS / REMOVE_OFFLINE_COMIC let the pages show and delete
// what was saved.

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    ORIGIN,
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
        offlineKey(filePath, 'cover'),
        offlineKey(filePath, 'complete')
    ]);
    assert.deepEqual(JSON.parse(await sw.caches.text(OFFLINE_CACHE, offlineKey(filePath, 'complete'))), { pageCount: 3 });
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

test('a page the server refuses rolls the download back, once the pages in flight settled, leaving other downloads alone', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(OFFLINE_CACHE, { [offlineKey('a.cbz.bak', 'page/1')]: image('other comic') });
    routeComic(sw.network, 'a.cbz', { pages: 6 });
    // Page 2 fails while pages 1 and 3 are still being fetched; page 3 answers last.
    let releasePage3;
    const page3 = new Promise(resolve => { releasePage3 = resolve; });
    sw.network.route(call => call.path === '/api/ComicReader/page' && call.query.page === '3', async () => {
        await page3;
        return image('a.cbz page 3');
    });
    sw.network.route(call => call.path === '/api/ComicReader/page' && call.query.page === '2', () => {
        setImmediate(releasePage3);
        return reply(500, { error: 'boom' });
    });

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages.at(0), { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 0, total: 6 });
    assert.deepEqual(client.messages.at(-1), { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'Request failed with status 500' });
    // No page after the failure was started, and the one still in flight was not left behind.
    assert.deepEqual(sw.network.callsTo('/api/ComicReader/page').map(call => call.query.page), ['1', '2', '3']);
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
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [offlineKey('a.cbz', 'info'), offlineKey('a.cbz', 'page/1'), offlineKey('a.cbz', 'complete')]);
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

        assert.equal(sw.caches.urls(OFFLINE_CACHE).length, 4);
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
        ],
        incomplete: []
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

    assert.deepEqual(port.messages, [{ type: 'OFFLINE_COMICS', comics: [{ filePath: 'a.cbz', pageCount: 1 }], incomplete: [] }]);
});

test('without a reply port the answer goes to the sending page, if any', async () => {
    const sw = await installedWorker();
    const client = createClient();

    await sw.message({ type: 'LIST_OFFLINE_COMICS' }, { source: client });
    await sw.message({ type: 'LIST_OFFLINE_COMICS' });
    await sw.message({ type: 'LIST_OFFLINE_COMICS' }, { source: { id: 'not-a-window' } });

    assert.deepEqual(client.messages, [{ type: 'OFFLINE_COMICS', comics: [], incomplete: [] }]);
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
        offlineKey('a.cbz.bak', 'info'), offlineKey('a.cbz.bak', 'page/1'), offlineKey('a.cbz.bak', 'cover'), offlineKey('a.cbz.bak', 'complete'),
        offlineKey('b.cbz', 'info'), offlineKey('b.cbz', 'page/1'), offlineKey('b.cbz', 'cover'), offlineKey('b.cbz', 'complete')
    ]);

    // Removal works without anyone to tell, and the sending page is told when there is no port.
    await sw.message({ type: 'REMOVE_OFFLINE_COMIC', filePath: 'b.cbz' });
    await sw.message({ type: 'REMOVE_OFFLINE_COMIC', filePath: 'a.cbz.bak' }, { source: client });
    assert.deepEqual(client.messages, [{ type: 'OFFLINE_COMIC_REMOVED', filePath: 'a.cbz.bak' }]);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), []);
});

// --- interrupted, resumed and shared downloads ---------------------------------

const DAY_MS = 24 * 60 * 60 * 1000;
const pageRoute = call => call.path === '/api/ComicReader/page';
const pagesRequested = sw => sw.network.callsTo('/api/ComicReader/page').map(call => Number(call.query.page));
const progress = client => client.messages.filter(message => message.type === 'OFFLINE_DOWNLOAD_PROGRESS').map(message => message.completed);

async function listOffline(sw) {
    const port = createPort();
    await sw.message({ type: 'LIST_OFFLINE_COMICS' }, { ports: [port] });
    return port.messages[0];
}

async function until(condition) {
    for (let i = 0; i < 200 && !condition(); i++) await new Promise(resolve => setImmediate(resolve));
    assert.ok(condition(), 'condition never became true');
}

// A download whose pages from `stopAt` on never answer, left running; resolves
// once every page before `stopAt` is stored. Restarting the worker afterwards
// is what the browser does when it stops a worker mid-download: nothing of the
// running download survives but what it stored.
async function stalledDownload(filePath, { pages, stopAt }) {
    const sw = await installedWorker();
    routeComic(sw.network, filePath, { pages });
    sw.network.route(call => pageRoute(call) && Number(call.query.page) >= stopAt, () => new Promise(() => {}));
    const client = createClient();
    sw.dispatch('message', { data: { type: 'DOWNLOAD_COMIC', filePath }, source: client, origin: ORIGIN, ports: [] });
    await until(() => progress(client).at(-1) === stopAt - 1);
    return { sw, client };
}

test('pages are downloaded three at a time', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 10 });
    let inFlight = 0;
    let most = 0;
    sw.network.route(pageRoute, async call => {
        most = Math.max(most, ++inFlight);
        await new Promise(resolve => setImmediate(resolve));
        inFlight--;
        return image(`a.cbz page ${call.query.page}`);
    });

    const client = await download(sw, 'a.cbz');

    assert.equal(most, 3);
    assert.deepEqual(pagesRequested(sw), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    assert.deepEqual(progress(client), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    assert.equal(client.messages.at(-1).type, 'OFFLINE_DOWNLOAD_COMPLETE');
});

test('a download that is still running is listed as incomplete, never as saved', async () => {
    const { sw } = await stalledDownload('a.cbz', { pages: 5, stopAt: 3 });

    assert.deepEqual(await listOffline(sw), {
        type: 'OFFLINE_COMICS',
        comics: [],
        incomplete: [{ filePath: 'a.cbz', savedPages: 2, pageCount: 5, active: true }]
    });
});

test('a download the browser stopped part-way is not saved, and is kept to be resumed', async () => {
    const { sw: stopped } = await stalledDownload('a.cbz', { pages: 5, stopAt: 3 });
    const sw = stopped.restart();

    assert.deepEqual(await listOffline(sw), {
        type: 'OFFLINE_COMICS',
        comics: [],
        incomplete: [{ filePath: 'a.cbz', savedPages: 2, pageCount: 5, active: false }]
    });
    assert.ok(!sw.caches.urls(OFFLINE_CACHE).includes(offlineKey('a.cbz', 'complete')));
});

test('saving an interrupted comic again fetches only the pages it is missing', async () => {
    const { sw: stopped } = await stalledDownload('a.cbz', { pages: 5, stopAt: 3 });
    const sw = stopped.restart();
    routeComic(sw.network, 'a.cbz', { pages: 5 });
    sw.network.calls.length = 0;

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(pagesRequested(sw), [3, 4, 5]);
    assert.deepEqual(progress(client), [2, 3, 4, 5]);
    assert.deepEqual(client.messages.at(-1), { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_COMPLETE', total: 5, fileName: 'a.cbz' });
    assert.deepEqual(await listOffline(sw), { type: 'OFFLINE_COMICS', comics: [{ filePath: 'a.cbz', pageCount: 5 }], incomplete: [] });
    assert.ok(!sw.caches.urls(OFFLINE_CACHE).includes(offlineKey('a.cbz', 'pending')));
});

test('pages kept from an earlier attempt are dropped when the comic changed its page count', async () => {
    const { sw: stopped } = await stalledDownload('a.cbz', { pages: 5, stopAt: 3 });
    const sw = stopped.restart();
    routeComic(sw.network, 'a.cbz', { pages: 4 });
    sw.network.calls.length = 0;

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(pagesRequested(sw), [1, 2, 3, 4]);
    assert.deepEqual(progress(client), [0, 1, 2, 3, 4]);
    assert.deepEqual(await listOffline(sw), { type: 'OFFLINE_COMICS', comics: [{ filePath: 'a.cbz', pageCount: 4 }], incomplete: [] });
});

test('a dropped connection keeps the pages saved so far and reports the download as resumable', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 3 });
    sw.network.route(call => pageRoute(call) && call.query.page === '3', () => {
        throw new TypeError('Failed to fetch');
    });

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages.at(-1), {
        filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'Failed to fetch', resumable: true, completed: 2, total: 3
    });
    assert.deepEqual(await listOffline(sw), {
        type: 'OFFLINE_COMICS',
        comics: [],
        incomplete: [{ filePath: 'a.cbz', savedPages: 2, pageCount: 3, active: false }]
    });

    // Resuming while still offline keeps what was saved, too.
    sw.network.offline = true;
    const retry = await download(sw, 'a.cbz');
    assert.deepEqual(retry.messages, [{
        filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'Failed to fetch', resumable: true, completed: 2, total: 3
    }]);
    assert.ok(sw.caches.urls(OFFLINE_CACHE).includes(offlineKey('a.cbz', 'page/2')));
});

// A body that delivers part of a page and then fails, as when the connection
// is lost after the headers arrived.
const droppedBody = () => new Response(
    new ReadableStream({
        start(controller) {
            controller.enqueue(new Uint8Array([1, 2, 3]));
        },
        pull(controller) {
            controller.error(new TypeError('network error'));
        }
    }),
    { status: 200, headers: { 'Content-Type': 'image/jpeg' } }
);

test('a connection lost during a page body keeps the pages saved so far, and resuming fetches only the missing ones', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 3 });
    sw.network.route(call => pageRoute(call) && call.query.page === '3', () => droppedBody());

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages.at(-1), {
        filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'network error', resumable: true, completed: 2, total: 3
    });
    const urls = sw.caches.urls(OFFLINE_CACHE);
    assert.ok(urls.includes(offlineKey('a.cbz', 'page/1')) && urls.includes(offlineKey('a.cbz', 'page/2')));
    assert.equal(urls.includes(offlineKey('a.cbz', 'page/3')), false, 'the half-received page is not stored');
    assert.deepEqual(await listOffline(sw), {
        type: 'OFFLINE_COMICS',
        comics: [],
        incomplete: [{ filePath: 'a.cbz', savedPages: 2, pageCount: 3, active: false }]
    });

    // The connection is back.
    routeComic(sw.network, 'a.cbz', { pages: 3 });
    sw.network.calls.length = 0;
    const retry = await download(sw, 'a.cbz');

    assert.deepEqual(pagesRequested(sw), [3]);
    assert.equal(retry.messages.at(-1).type, 'OFFLINE_DOWNLOAD_COMPLETE');
    assert.deepEqual(await listOffline(sw), { type: 'OFFLINE_COMICS', comics: [{ filePath: 'a.cbz', pageCount: 3 }], incomplete: [] });
});

test('a connection lost during the info body of a resumed download keeps what was saved', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 3 });
    sw.network.route(call => pageRoute(call) && call.query.page === '3', () => {
        throw new TypeError('Failed to fetch');
    });
    await download(sw, 'a.cbz');

    sw.network.route(call => call.path === '/api/ComicReader/info', () => droppedBody());
    const retry = await download(sw, 'a.cbz');

    assert.deepEqual(retry.messages.at(-1), {
        filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'network error', resumable: true, completed: 2, total: 3
    });
    assert.ok(sw.caches.urls(OFFLINE_CACHE).includes(offlineKey('a.cbz', 'page/2')));
    assert.deepEqual((await listOffline(sw)).incomplete, [{ filePath: 'a.cbz', savedPages: 2, pageCount: 3, active: false }]);
});

test('when pages fail together, a lost connection decides over a refused page', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 3 });
    // All three start together; the refused page's lane is reported first.
    sw.network.route(call => pageRoute(call) && call.query.page === '1', reply(502, 'Bad Gateway'));
    sw.network.route(call => pageRoute(call) && call.query.page === '2', () => droppedBody());

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages.at(-1), {
        filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'network error', resumable: true, completed: 1, total: 3
    });
    assert.ok(sw.caches.urls(OFFLINE_CACHE).includes(offlineKey('a.cbz', 'page/3')));
});

test('a storage failure while saving a page still rolls the download back', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 3 });
    const cache = await sw.caches.open(OFFLINE_CACHE);
    const put = cache.put.bind(cache);
    cache.put = async (key, response) => {
        if (String(key).endsWith('/page/3')) throw new DOMException('Quota exceeded', 'QuotaExceededError');
        return put(key, response);
    };

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages.at(-1), { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'Quota exceeded' });
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), []);
});

test('a dropped connection before any page was saved leaves nothing behind', async () => {
    const sw = await installedWorker();
    sw.network.offline = true;

    const client = await download(sw, 'a.cbz');

    assert.deepEqual(client.messages, [{ filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_FAILED', error: 'Failed to fetch' }]);
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), []);
});

test('asking again while a comic is downloading joins that download', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'a.cbz', { pages: 4 });
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    sw.network.route(call => pageRoute(call) && call.query.page === '3', async () => {
        await gate;
        return image('a.cbz page 3');
    });
    const first = createClient('window-1');
    const second = createClient('window-2');
    const firstDone = sw.message({ type: 'DOWNLOAD_COMIC', filePath: 'a.cbz' }, { source: first });
    // Every page but the third is saved (pages are fetched three at a time).
    await until(() => progress(first).at(-1) === 3);

    const secondDone = sw.message({ type: 'DOWNLOAD_COMIC', filePath: 'a.cbz' }, { source: second });
    await sw.idle();
    // The page that joined learns how far the download got.
    assert.deepEqual(second.messages, [{ filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_PROGRESS', completed: 3, total: 4 }]);
    release();
    await Promise.all([firstDone, secondDone]);

    for (const client of [first, second]) {
        assert.deepEqual(client.messages.at(-1), { filePath: 'a.cbz', type: 'OFFLINE_DOWNLOAD_COMPLETE', total: 4, fileName: 'a.cbz' });
    }
    assert.equal(sw.network.callsTo('/api/ComicReader/info').length, 1);
    assert.deepEqual(pagesRequested(sw), [1, 2, 3, 4]);
});

test('interrupted downloads nobody resumed for a week, or that saved no page, are removed', async () => {
    const sw = await installedWorker();
    const pending = updatedAt => ({ updatedAt });
    await sw.caches.seed(OFFLINE_CACHE, {
        [offlineKey('old.cbz', 'pending')]: pending(Date.now() - 8 * DAY_MS),
        [offlineKey('old.cbz', 'info')]: { pageCount: 3 },
        [offlineKey('old.cbz', 'page/1')]: image('old page 1'),
        [offlineKey('recent.cbz', 'pending')]: pending(Date.now() - 6 * DAY_MS),
        [offlineKey('recent.cbz', 'info')]: { pageCount: 3 },
        [offlineKey('recent.cbz', 'page/1')]: image('recent page 1'),
        [offlineKey('pageless.cbz', 'pending')]: pending(Date.now()),
        [offlineKey('pageless.cbz', 'info')]: { pageCount: 3 },
        [offlineKey('unknown.cbz', 'pending')]: pending(Date.now()),
        [offlineKey('unknown.cbz', 'page/1')]: image('unknown page 1')
    });

    assert.deepEqual(await listOffline(sw), {
        type: 'OFFLINE_COMICS',
        comics: [],
        incomplete: [{ filePath: 'recent.cbz', savedPages: 1, pageCount: 3, active: false }]
    });
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [
        offlineKey('recent.cbz', 'pending'), offlineKey('recent.cbz', 'info'), offlineKey('recent.cbz', 'page/1')
    ]);
});

test('downloads saved before completion was recorded are kept when complete and dropped when not', async () => {
    const sw = await installedWorker();
    await sw.caches.seed(OFFLINE_CACHE, {
        [offlineKey('whole.cbz', 'info')]: { pageCount: 2 },
        [offlineKey('whole.cbz', 'page/1')]: image('page 1'),
        [offlineKey('whole.cbz', 'page/2')]: image('page 2'),
        [offlineKey('whole.cbz', 'cover')]: image('cover'),
        [offlineKey('partial.cbz', 'info')]: { pageCount: 3 },
        [offlineKey('partial.cbz', 'page/1')]: image('page 1'),
        [offlineKey('partial.cbz', 'page/3')]: image('page 3')
    });

    assert.deepEqual(await listOffline(sw), { type: 'OFFLINE_COMICS', comics: [{ filePath: 'whole.cbz', pageCount: 2 }], incomplete: [] });
    assert.deepEqual(sw.caches.urls(OFFLINE_CACHE), [
        offlineKey('whole.cbz', 'info'), offlineKey('whole.cbz', 'page/1'), offlineKey('whole.cbz', 'page/2'),
        offlineKey('whole.cbz', 'cover'), offlineKey('whole.cbz', 'complete')
    ]);
});
