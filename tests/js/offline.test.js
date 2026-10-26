'use strict';

// The offline fallback page (offline.html + js/pages/offline.js): the
// connection status, the retry button, and the list of comics the service
// worker saved for offline reading (asked for over a MessageChannel).

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage, plain } = require('./support/dom');
const { FakeMessageChannel, installedWorker, routeComic, createClient } = require('./support/sw-helpers');

const WAITING = 'Waiting for a network connection…';
const RESTORED = 'Connection restored — try again.';

/** A navigator.serviceWorker.controller that records what the page posts to it. */
function recordingController() {
    const controller = {
        messages: [],
        postMessage(message, transfer = []) {
            controller.messages.push({ message, transfer: [...transfer] });
        }
    };
    return controller;
}

/**
 * Open offline.html with navigator.onLine reporting `online` and `controller`
 * as the controlling service worker. page.channels lists the MessageChannels
 * the page created; page.setOnline(flag) changes the connection and fires
 * the matching window event.
 */
function openOffline({ online = false, controller = null, url = 'http://localhost/offline.html', ...options } = {}) {
    const connection = { online };
    const channels = [];
    class RecordingChannel extends FakeMessageChannel {
        constructor() {
            super();
            channels.push(this);
        }
    }
    const page = loadPage({
        html: 'offline.html',
        url,
        globals: { MessageChannel: RecordingChannel },
        beforeScripts(window) {
            Object.defineProperty(window.navigator, 'onLine', { get: () => connection.online, configurable: true });
            if (controller) window.navigator.serviceWorker.controller = controller;
        },
        ...options
    });
    page.channels = channels;
    page.setOnline = flag => {
        connection.online = flag;
        page.window.dispatchEvent(new page.window.Event(flag ? 'online' : 'offline'));
    };
    return page;
}

/** The rendered download list: each link's target, comic name and page count. */
function listed(page) {
    return page.$$('#downloadsList > li > a').map(link => ({
        href: link.getAttribute('href'),
        name: link.firstChild.nodeValue,
        pages: link.querySelector('.pages').textContent
    }));
}

// --- connection status and retry -----------------------------------------------

test('offline, the page says it is waiting for a connection without touching the network', async () => {
    const page = openOffline({ online: false });
    await page.settle();

    assert.equal(page.$('#statusText').textContent, WAITING);
    assert.deepEqual(page.fetchCalls, []);
    assert.equal(page.$('#downloads').hidden, true);
    assert.deepEqual(page.errors, []);
});

test('the status follows the connection as it comes and goes', async () => {
    const page = openOffline({ online: true });
    await page.settle();
    const status = page.$('#statusText');
    assert.equal(status.textContent, RESTORED);

    page.setOnline(false);
    assert.equal(status.textContent, WAITING);

    page.setOnline(true);
    assert.equal(status.textContent, RESTORED);
});

test('Try again reloads the page the browser could not open', async () => {
    // The service worker answers a failed navigation with offline.html, so the
    // document keeps the address that was asked for.
    const page = openOffline({ url: 'http://localhost/reader.html?file=a.cbz' });
    await page.settle();

    page.click('#retryButton');

    assert.deepEqual(page.navigations, [{ url: 'http://localhost/reader.html?file=a.cbz', reload: true }]);
});

// --- downloaded comics ---------------------------------------------------------

test('the page asks the service worker for the downloaded comics and lists them by path', async () => {
    const controller = recordingController();
    const page = openOffline({ controller });
    await page.settle();

    assert.equal(controller.messages.length, 1);
    const [{ message, transfer }] = controller.messages;
    assert.deepEqual(plain(message), { type: 'LIST_OFFLINE_COMICS' });
    assert.deepEqual(transfer, [page.channels[0].port2]);
    assert.equal(page.$('#downloads').hidden, true, 'nothing is shown before the worker answers');

    transfer[0].postMessage({
        type: 'OFFLINE_COMICS',
        comics: [
            { filePath: 'Series/Zeta #2.cbz', pageCount: 12 },
            { filePath: 'C:\\Comics\\Beta.cbz', pageCount: 1 },
            { filePath: 'Odd/', pageCount: 2 },
            { filePath: 'alpha.cbz', pageCount: 0 }
        ]
    });
    await page.settle();

    assert.equal(page.$('#downloads').hidden, false);
    assert.deepEqual(listed(page), [
        // Sorted like people read (localeCompare), not by character code.
        { href: '/reader.html?file=alpha.cbz', name: 'alpha.cbz', pages: '0 pages saved' },
        // Only the file name is shown, for / and \ separated paths alike...
        { href: '/reader.html?file=C%3A%5CComics%5CBeta.cbz', name: 'Beta.cbz', pages: '1 page saved' },
        // ...unless the path has no file name, then all of it is.
        { href: '/reader.html?file=Odd%2F', name: 'Odd/', pages: '2 pages saved' },
        { href: '/reader.html?file=Series%2FZeta%20%232.cbz', name: 'Zeta #2.cbz', pages: '12 pages saved' }
    ]);
    assert.deepEqual(page.errors, []);
});

test('a comic path is shown as text, never run as markup', async () => {
    const controller = recordingController();
    const page = openOffline({ controller });
    await page.settle();
    const filePath = 'Evil/<img src=x onerror="window.pwned = true">.cbz';

    controller.messages[0].transfer[0].postMessage({ type: 'OFFLINE_COMICS', comics: [{ filePath, pageCount: 1 }] });
    await page.settle();

    assert.equal(page.$('#downloadsList img'), null);
    assert.equal(listed(page)[0].name, '<img src=x onerror="window.pwned = true">.cbz');
    assert.equal(page.run('window.pwned'), undefined);
});

test('answers without comics leave the list hidden', async () => {
    const answers = [
        { type: 'OFFLINE_COMICS', comics: [] },
        { type: 'OFFLINE_COMICS' },
        { type: 'OFFLINE_COMIC_REMOVED', filePath: 'a.cbz' },
        // Only an OFFLINE_COMICS answer is a list.
        { type: 'DOWNLOAD_COMPLETE', comics: [{ filePath: 'a.cbz', pageCount: 1 }] },
        null
    ];
    for (const answer of answers) {
        const controller = recordingController();
        const page = openOffline({ controller });
        await page.settle();

        controller.messages[0].transfer[0].postMessage(answer);
        await page.settle();

        const label = JSON.stringify(answer);
        assert.equal(page.$('#downloads').hidden, true, label);
        assert.equal(page.$$('#downloadsList li').length, 0, label);
        assert.deepEqual(page.errors, [], label);
        page.close();
    }
});

test('without a controlling service worker the page asks nobody', async () => {
    // Not controlled (e.g. a hard reload), and a browser without service workers.
    for (const options of [{}, { serviceWorker: false }]) {
        const page = openOffline(options);
        await page.settle();

        assert.deepEqual(page.channels, []);
        assert.equal(page.$('#statusText').textContent, WAITING);
        assert.equal(page.$('#downloads').hidden, true);
        assert.deepEqual(page.errors, []);
        page.close();
    }
});

test('the list shows what the real service worker saved for offline reading', async () => {
    const sw = await installedWorker();
    routeComic(sw.network, 'Series/Vol 1 #1.cbz', { pages: 3 });
    routeComic(sw.network, 'One-shot.cbz', { pages: 1 });
    const source = createClient();
    await sw.message({ type: 'DOWNLOAD_COMIC', filePath: 'Series/Vol 1 #1.cbz' }, { source });
    await sw.message({ type: 'DOWNLOAD_COMIC', filePath: 'One-shot.cbz' }, { source });

    // The controller hands the page's message and port to the worker, as the browser does.
    const failures = [];
    const controller = {
        postMessage(message, transfer) {
            sw.message(structuredClone(message), { ports: [...transfer] }).catch(error => failures.push(error));
        }
    };
    const page = openOffline({ controller });
    await page.waitFor(() => !page.$('#downloads').hidden);

    assert.deepEqual(listed(page), [
        { href: '/reader.html?file=One-shot.cbz', name: 'One-shot.cbz', pages: '1 page saved' },
        { href: '/reader.html?file=Series%2FVol%201%20%231.cbz', name: 'Vol 1 #1.cbz', pages: '3 pages saved' }
    ]);
    assert.deepEqual(failures, []);
    // Everything came from the worker's offline cache; the page made no requests.
    assert.deepEqual(page.fetchCalls, []);
});
