'use strict';

// Saving a comic for offline reading from the reader (js/pages/reader.js):
// the button talks to the controlling service worker (sw.js), which keeps the
// downloaded pages, and reports download progress back as messages.

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    FILE, NEXT_FILE, openReader, toasts, serviceWorkerController, withController
} = require('./support/reader-helpers');

const offlineButton = page => {
    const button = page.$('#offlineBtn');
    return {
        hidden: button.hidden,
        label: page.$('#offlineBtnLabel').textContent,
        title: button.title,
        ariaLabel: button.getAttribute('aria-label'),
        disabled: button.disabled
    };
};

const SAVE = {
    hidden: false,
    label: 'Save offline',
    title: 'Save this comic for offline reading',
    ariaLabel: 'Save this comic for offline reading',
    disabled: false
};
const SAVED = {
    hidden: false,
    label: 'Saved',
    title: 'Remove this comic from offline storage',
    ariaLabel: 'Remove this comic from offline storage',
    disabled: false
};
const savingAs = label => ({
    hidden: false,
    label,
    title: 'Downloading this comic for offline reading',
    ariaLabel: 'Downloading this comic for offline reading',
    disabled: true
});

async function openWithWorker(workerOptions, options = {}) {
    const controller = serviceWorkerController(workerOptions);
    const page = await openReader({ ...options, beforeScripts: withController(controller) });
    await page.waitFor(() => !page.$('#offlineBtn').hidden);
    return { page, controller };
}

const workerMessage = (page, data) => page.window.navigator.serviceWorker.emit('message', { data });

test('without a controlling service worker the reader registers one and offers no download', async () => {
    const page = await openReader();
    await page.settle();

    assert.deepEqual(page.serviceWorkerRegistrations.map(({ url, options }) => ({ url, scope: options.scope })), [{ url: '/sw.js', scope: '/' }]);
    assert.equal(page.$('#offlineBtn').hidden, true);
});

test('the button says whether this comic is already saved for offline reading', async () => {
    const { page: saved, controller } = await openWithWorker({ offline: [NEXT_FILE, FILE] });
    assert.deepEqual(offlineButton(saved), SAVED);
    assert.deepEqual(controller.messages, [{ type: 'LIST_OFFLINE_COMICS' }]);
    // An existing worker is used as is.
    assert.deepEqual(saved.serviceWorkerRegistrations, []);

    const { page: unsaved } = await openWithWorker({ offline: [NEXT_FILE] });
    assert.deepEqual(offlineButton(unsaved), SAVE);
});

test('saving downloads the comic through the worker, showing its progress', async () => {
    const { page, controller } = await openWithWorker();

    page.click('#offlineBtn');
    assert.deepEqual(offlineButton(page), savingAs('Saving…'));
    // The worker cannot read the session itself, so it is handed the auth header.
    assert.deepEqual(controller.messages.at(-1), {
        type: 'DOWNLOAD_COMIC',
        filePath: FILE,
        headers: { Authorization: `Bearer ${page.window.localStorage.getItem('jwt_token')}` }
    });

    // Clicking again while downloading does nothing.
    page.click('#offlineBtn');
    assert.equal(controller.messages.length, 2);

    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_PROGRESS', filePath: FILE, completed: 3, total: 8 });
    assert.deepEqual(offlineButton(page), savingAs('Saving 38%'));
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_PROGRESS', filePath: FILE, completed: 0, total: 0 });
    assert.equal(offlineButton(page).label, 'Saving 0%');

    // Messages about other comics, or without data, are not for this reader.
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: NEXT_FILE });
    workerMessage(page, null);
    workerMessage(page, { type: 'SOMETHING_ELSE', filePath: FILE });
    assert.equal(offlineButton(page).label, 'Saving 0%');

    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: FILE });
    assert.deepEqual(offlineButton(page), SAVED);
    assert.deepEqual(toasts(page), [{ message: 'Available for offline reading', type: 'success' }]);
});

test('an Authelia session hands the worker no bearer header; its cookies are enough', async () => {
    const { page, controller } = await openWithWorker({}, { token: null, localStorage: { authelia_authenticated: 'true' } });

    page.click('#offlineBtn');
    assert.deepEqual(controller.messages.at(-1), { type: 'DOWNLOAD_COMIC', filePath: FILE, headers: {} });
    assert.deepEqual(offlineButton(page), savingAs('Saving…'));
});

test('a saved comic can be removed from offline storage again', async () => {
    const { page, controller } = await openWithWorker({ offline: [FILE] });

    page.click('#offlineBtn');
    await page.waitFor(() => offlineButton(page).label === 'Save offline');
    assert.deepEqual(controller.messages.at(-1), { type: 'REMOVE_OFFLINE_COMIC', filePath: FILE });
    assert.deepEqual(offlineButton(page), SAVE);
    assert.deepEqual(toasts(page), [{ message: 'Removed from offline storage', type: 'info' }]);
});

test('a failed download is reported and can be started again', async () => {
    const { page, controller } = await openWithWorker();

    page.click('#offlineBtn');
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_FAILED', filePath: FILE, error: 'Quota exceeded' });
    assert.deepEqual(offlineButton(page), SAVE);
    assert.deepEqual(toasts(page), [{ message: 'Offline download failed', type: 'error' }]);

    page.click('#offlineBtn');
    assert.equal(controller.messages.filter(message => message.type === 'DOWNLOAD_COMIC').length, 2);
});

test('a status update that arrives mid-download leaves the progress showing', async () => {
    const { page, controller } = await openWithWorker();
    page.click('#offlineBtn');
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_PROGRESS', filePath: FILE, completed: 1, total: 2 });

    // The page asks for the saved list again (as it would on start-up) while downloading.
    await page.run('refreshOfflineStatus()');
    assert.deepEqual(controller.messages.at(-1), { type: 'LIST_OFFLINE_COMICS' });
    assert.deepEqual(offlineButton(page), savingAs('Saving 50%'));
});

test('a worker that never answers is given up on after five seconds', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const controller = serviceWorkerController({ reply: false, offline: [FILE] });
    const page = await openReader({ beforeScripts: withController(controller) });

    t.mock.timers.tick(4999);
    await page.settle();
    assert.equal(page.$('#offlineBtn').hidden, true);

    t.mock.timers.tick(1);
    await page.waitFor(() => !page.$('#offlineBtn').hidden);
    // No answer means nothing is known to be saved.
    assert.deepEqual(offlineButton(page), SAVE);
});

test('a worker that goes away leaves the button explaining downloads are unavailable', async () => {
    const { page, controller } = await openWithWorker();
    page.window.navigator.serviceWorker.controller = null;

    page.click('#offlineBtn');
    assert.deepEqual(toasts(page), [{ message: 'Offline downloads are not available in this browser', type: 'error' }]);
    assert.deepEqual(controller.messages, [{ type: 'LIST_OFFLINE_COMICS' }]);
    assert.deepEqual(offlineButton(page), SAVE);
});

test('browsers without service workers read online only', async () => {
    const page = await openReader({ serviceWorker: false });
    await page.settle();

    assert.equal(page.$('#offlineBtn').hidden, true);
    page.click('#offlineBtn');
    assert.deepEqual(toasts(page), [{ message: 'Offline downloads are not available in this browser', type: 'error' }]);
    assert.deepEqual(page.errors, []);
});

test('a service worker that cannot be registered is logged and the reader carries on', async () => {
    const page = await openReader({
        beforeScripts: window => {
            window.navigator.serviceWorker.register = async () => { throw new Error('SecurityError: insecure origin'); };
        }
    });
    await page.settle();

    const note = page.consoleMessages.find(m => m.args[0] === 'Offline reading unavailable:');
    assert.equal(note.args[1].message, 'SecurityError: insecure origin');
    assert.equal(page.$('#offlineBtn').hidden, true);
    assert.equal(page.$('#readerContent img.page-image').alt, 'Page 1');
});

test('without its label element the offline button is never shown', async () => {
    const controller = serviceWorkerController({ offline: [FILE] });
    const page = await openReader({
        beforeScripts: window => {
            withController(controller)(window);
            window.document.getElementById('offlineBtnLabel').remove();
        }
    });
    await page.waitFor(() => controller.messages.length === 1);
    await page.settle();

    assert.equal(page.$('#offlineBtn').hidden, true);
});
