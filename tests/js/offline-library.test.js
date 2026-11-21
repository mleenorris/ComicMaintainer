'use strict';

// Offline reading from the library (js/app/offline.js): saving issues without
// opening them (from an issue's menu, a series, or the selected series), the
// marks of the saved ones, and the Offline Comics dialog, which also opens by
// itself when the app starts with no connection.

const test = require('node:test');
const assert = require('node:assert/strict');
const h = require('./support/jobs-helpers');
const { clickIssueAction } = require('./support/email-helpers');
const { serviceWorkerController, withController } = require('./support/reader-helpers');

const A = h.ISSUE_A;
const B = h.ISSUE_B;
const C = 'Batman/Batman 003.cbz';
const workerMessage = (page, data) => page.window.navigator.serviceWorker.emit('message', { data });
const mark = (page, path) => page.$$('.series-issue-offline').find(element => element.dataset.offlinePath === path);
const markText = (page, path) => {
    const element = mark(page, path);
    return element.hidden ? '' : element.textContent;
};
const downloads = controller => controller.messages.filter(message => message.type === 'DOWNLOAD_COMIC');
const seriesButton = (page, label) => page.$$('.series-detail-actions > button').find(button => button.textContent.trim() === label);

async function seriesWithWorker(t, { saved = [], incomplete = [], reply = true, ...options } = {}) {
    const controller = serviceWorkerController({ offline: saved, incomplete, reply });
    const page = await h.bootSeriesDetail(t, { beforeScripts: withController(controller), ...options });
    await page.settle();
    return { page, controller };
}

test('the series page marks the saved issues, and an issue menu removes a saved copy', async (t) => {
    const { page, controller } = await seriesWithWorker(t, { saved: [A] });

    assert.equal(markText(page, A), 'Offline');
    assert.equal(markText(page, B), '');

    clickIssueAction(page, A, 'Remove Offline Copy');
    await page.settle();

    assert.deepEqual(controller.messages.at(-1), { type: 'REMOVE_OFFLINE_COMIC', filePath: A });
    assert.equal(markText(page, A), '');
    assert.ok(h.messages(page).some(message => message.text === 'Removed from offline storage'));
});

test("an issue's menu saves it, following the worker's progress to the end", async (t) => {
    const { page, controller } = await seriesWithWorker(t);

    clickIssueAction(page, B, 'Save Offline');
    await page.settle();

    const [download] = downloads(controller);
    assert.equal(download.filePath, B);
    assert.match(download.headers.Authorization, /^Bearer /);
    assert.equal(markText(page, B), 'Saving…');

    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_PROGRESS', filePath: B, completed: 0, total: 0 });
    assert.equal(markText(page, B), 'Saving 0%', 'a comic whose page count is not known yet');
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_PROGRESS', filePath: B, completed: 1, total: 4 });
    assert.equal(markText(page, B), 'Saving 25%');
    // Another comic's progress (the reader's download) is not this page's.
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_PROGRESS', filePath: A, completed: 1, total: 4 });
    assert.equal(markText(page, A), '');

    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: B, total: 4 });
    assert.equal(markText(page, B), 'Offline');
    assert.ok(h.messages(page).some(message => message.text === 'Batman 002.cbz is available for offline reading'));

    // Its menu now removes the copy; asking to save it again does nothing.
    await page.run(`saveComicsOffline([${JSON.stringify(B)}])`);
    assert.equal(downloads(controller).length, 1);

    // Signed in through Authelia, the worker's requests carry its cookies, not a token.
    page.window.localStorage.setItem('authelia_authenticated', 'true');
    await page.run(`saveComicsOffline([${JSON.stringify(A)}])`);
    assert.deepEqual(downloads(controller).at(-1), { type: 'DOWNLOAD_COMIC', filePath: A, headers: {} });
});

test('a series is saved one issue at a time, once the count is confirmed', async (t) => {
    const { page, controller } = await seriesWithWorker(t);

    h.answerConfirms(page, false);
    page.click(seriesButton(page, 'Save Offline'));
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), ['Save 2 issues on this device for reading offline? They take up storage space until you remove them.']);
    assert.equal(downloads(controller).length, 0, 'declined: nothing is saved');

    page.click(seriesButton(page, 'Save Offline'));
    await page.settle();
    assert.deepEqual(downloads(controller).map(message => message.filePath), [A]);
    assert.equal(markText(page, B), 'Queued');
    assert.equal(page.window.sessionStorage.getItem('offlineSaveQueue'), JSON.stringify([A, B]), 'kept for the tab');

    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: A, total: 3 });
    assert.ok(h.messages(page).some(message => message.text === 'Batman 001.cbz is saved for offline reading; 1 to go'));
    assert.deepEqual(downloads(controller).map(message => message.filePath), [A, B]);
    assert.equal(markText(page, B), 'Saving…');
});

test('the saves left undone when the page was left resume once the library is back', async (t) => {
    const controller = serviceWorkerController({ offline: [A] });
    const page = await h.bootSeriesDetail(t, {
        beforeScripts(window) {
            withController(controller)(window);
            window.sessionStorage.setItem('offlineSaveQueue', JSON.stringify([A, B, C]));
        }
    });
    await page.settle();

    assert.deepEqual(downloads(controller).map(message => message.filePath), [B], 'the one saved meanwhile is skipped');
    assert.ok(h.messages(page).some(message => message.text === 'Saving 2 more issues for offline reading…'));
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: B, total: 2 });
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: C, total: 2 });
    assert.equal(page.window.sessionStorage.getItem('offlineSaveQueue'), null);
});

test('an interrupted save keeps its pages to resume, and a failed one keeps none', async (t) => {
    const { page, controller } = await seriesWithWorker(t);
    await page.run(`saveComicsOffline([${JSON.stringify(A)}])`);

    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_FAILED', filePath: A, resumable: true, completed: 2, total: 5 });
    assert.equal(markText(page, A), 'Offline 2/5');
    assert.ok(h.messages(page).some(message => message.text === 'Batman 001.cbz was not saved completely (2 of 5 pages); save it again to finish'));

    clickIssueAction(page, A, 'Resume Offline Save (2/5)');
    await page.settle();
    assert.equal(downloads(controller).length, 2);
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_FAILED', filePath: A, error: 'Storage is full' });
    assert.equal(markText(page, A), '');
    assert.ok(h.messages(page).some(message => message.text === 'Batman 001.cbz could not be saved for offline reading'));
});

test('a save the worker stopped reporting is checked on, and ends as the worker says', async (t) => {
    const { page, controller } = await seriesWithWorker(t);
    await page.run(`saveComicsOffline([${JSON.stringify(A)}])`);

    // Still running: the progress the worker gives is shown, and checked again later.
    controller.incomplete = [{ filePath: A, savedPages: 1, pageCount: 4, active: true }];
    t.mock.timers.tick(30000);
    await page.settle();
    assert.equal(markText(page, A), 'Saving 25%');

    // Stopped with pages saved.
    controller.incomplete = [{ filePath: A, savedPages: 3, pageCount: 4, active: false }];
    t.mock.timers.tick(30000);
    await page.settle();
    assert.equal(markText(page, A), 'Offline 3/4');

    // Finished without the page hearing.
    await page.run(`saveComicsOffline([${JSON.stringify(B)}])`);
    controller.saved.add(B);
    t.mock.timers.tick(30000);
    await page.settle();
    assert.equal(markText(page, B), 'Offline');

    // Finished while the check waited for the worker's answer: the check stands down.
    await page.run(`saveComicsOffline([${JSON.stringify(A)}])`);
    t.mock.timers.tick(30000);
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: A, total: 4 });
    await page.settle();
    assert.equal(markText(page, A), 'Offline');

    // Gone without a trace.
    controller.saved.delete(B);
    await page.run(`removeOfflineCopy(${JSON.stringify(B)})`);
    await page.run(`saveComicsOffline([${JSON.stringify(B)}])`);
    t.mock.timers.tick(30000);
    await page.settle();
    assert.equal(markText(page, B), '');
    assert.ok(h.messages(page).some(message => message.text === 'Batman 002.cbz could not be saved for offline reading'));
});

test('the selected series are saved once their issues are known', async (t) => {
    const { page, controller } = await seriesWithWorker(t);
    await page.run(`selectedFiles.add(${JSON.stringify(A)}); selectedFiles.add(${JSON.stringify(B)}); saveSelectedOffline()`);
    await page.settle();
    assert.deepEqual(downloads(controller).map(message => message.filePath), [A]);

    await page.run('selectedFiles.clear(); saveSelectedOffline()');
    assert.ok(h.messages(page).some(message => message.text === 'Select the series to save for offline reading first.'));
});

test('without a service worker nothing is saved, and the dialog says why', async (t) => {
    const page = await h.bootSeriesDetail(t);
    await page.settle();

    page.click(seriesButton(page, 'Save Offline'));
    await page.settle();
    assert.ok(h.messages(page).some(message => message.text === 'Offline reading is not available in this browser.'));

    await page.run('openOfflineComicsModal()');
    assert.equal(page.$('#offlineComicsSummary').textContent, 'Offline reading is not available in this browser.');
});

test('the Offline Comics dialog lists what is saved, to read, resume or remove', async (t) => {
    const { page, controller } = await seriesWithWorker(t, {
        saved: [B],
        incomplete: [{ filePath: A, savedPages: 1, pageCount: 3, active: false }]
    });
    page.click('#sidebarOfflineBtn');
    await page.settle();

    assert.ok(page.$('#offlineComicsModal').classList.contains('active'));
    assert.equal(page.$('#offlineComicsSummary').textContent, '1 comic saved on this device.');
    const rows = () => page.$$('#offlineComicsList .offline-comic-row').map(row => ({
        name: row.querySelector('strong').textContent,
        state: row.querySelector('.offline-comic-state').textContent,
        actions: [...row.querySelectorAll('button')].map(button => button.textContent.trim())
    }));
    assert.deepEqual(rows(), [
        { name: 'Batman 001.cbz', state: 'Offline 1/3', actions: ['Resume', 'Remove'] },
        { name: 'Batman 002.cbz', state: 'Offline', actions: ['Read', 'Remove'] }
    ]);

    page.click(page.$$('#offlineComicsList button').find(button => button.textContent.trim() === 'Resume'));
    await page.settle();
    assert.deepEqual(downloads(controller).map(message => message.filePath), [A]);
    assert.equal(rows()[0].state, 'Saving…', 'the open dialog follows the save');

    page.click(page.$$('#offlineComicsList button').filter(button => button.textContent.trim() === 'Remove').at(-1));
    await page.settle();
    assert.deepEqual(rows().map(row => row.name), ['Batman 001.cbz']);

    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: A, total: 3 });
    assert.equal(page.$('#offlineComicsList small').textContent, 'Batman · 3 pages');

    page.click(page.$$('#offlineComicsList button').find(button => button.textContent.trim() === 'Remove'));
    await page.settle();
    assert.equal(rows().length, 0);
    assert.equal(page.$('#offlineComicsEmpty').hidden, false);
});

test('the storage the saved comics use is reported, and whether the browser may clear them', async (t) => {
    const controller = serviceWorkerController({ offline: [A] });
    const page = await h.bootSeriesDetail(t, {
        beforeScripts(window) {
            withController(controller)(window);
            Object.defineProperty(window.navigator, 'storage', {
                configurable: true,
                value: {
                    estimate: async () => ({ usage: 50 * 1024 * 1024, quota: 2 * 1024 * 1024 * 1024 }),
                    persisted: async () => false,
                    persist: async () => true
                }
            });
        }
    });
    await page.run('openOfflineComicsModal()');
    assert.equal(page.$('#offlineComicsSummary').textContent,
        '1 comic saved on this device. This site uses 50.0 MB of the 2.0 GB the browser allows it. The browser may remove them if the device runs short of space.');
    // Kept for good once the browser agrees to.
    page.window.navigator.storage.persisted = async () => true;
    await page.run('openOfflineComicsModal()');
    assert.equal(page.$('#offlineComicsSummary').textContent,
        '1 comic saved on this device. This site uses 50.0 MB of the 2.0 GB the browser allows it.');

    // An answer the worker never gives leaves the removal undone.
    controller.saved.add(B);
    const silent = serviceWorkerController({ reply: false });
    page.window.navigator.serviceWorker.controller = silent;
    const removal = page.run(`removeOfflineCopy(${JSON.stringify(A)})`);
    t.mock.timers.tick(5000);
    await removal;
    assert.ok(h.messages(page).some(message => message.text === 'The offline copy could not be removed.'));
});

test('an app started with no connection opens the comics saved on this device', async () => {
    const controller = serviceWorkerController({ offline: [A] });
    const { bootApp } = require('./support/app');
    const page = await bootApp({
        hash: '#/library',
        beforeScripts(window) {
            withController(controller)(window);
            Object.defineProperty(window.navigator, 'onLine', { value: false, configurable: true });
            window.sessionStorage.setItem('offlineSaveQueue', JSON.stringify([B]));
        }
    });
    await page.settle();
    assert.equal(page.window.sessionStorage.getItem('offlineSaveQueue'), JSON.stringify([B]), 'the saves left undone wait for the connection');

    assert.ok(page.$('#offlineComicsModal').classList.contains('active'));
    assert.deepEqual(page.$$('#offlineComicsList strong').map(name => name.textContent), ['Batman 001.cbz']);
    assert.equal(h.requests(page, 'GET', '/api/auth/status').length, 0, 'nothing is asked of a server it cannot reach');
    assert.ok(page.$('#appOfflineBanner'));

    // The connection is back: the library starts where the page is, without a reload.
    Object.defineProperty(page.window.navigator, 'onLine', { value: true, configurable: true });
    page.window.dispatchEvent(new page.window.Event('online'));
    await page.settle();
    assert.equal(h.requests(page, 'GET', '/api/auth/status').length, 1);
    assert.equal(h.requests(page, 'GET', '/api/auth/user').length, 1, 'the app goes on past the sign-in check');
    assert.equal(page.navigations.filter(navigation => navigation.reload).length, 0);
    assert.equal(page.$('#appOfflineBanner'), null);
});

test('a worker taking over the page is asked again what it holds', async (t) => {
    const { page } = await seriesWithWorker(t);
    assert.equal(markText(page, A), '');

    page.window.navigator.serviceWorker.controller = serviceWorkerController({ offline: [A] });
    page.window.navigator.serviceWorker.emit('controllerchange');
    await page.settle();
    assert.equal(markText(page, A), 'Offline');
});

test('an issue already being saved is not asked for twice', async (t) => {
    const { page, controller } = await seriesWithWorker(t);
    await page.run(`saveComicsOffline([${JSON.stringify(A)}, ${JSON.stringify(B)}])`);

    for (const [path, label] of [[A, 'Saving Offline…'], [B, 'Saving Offline…']]) {
        clickIssueAction(page, path, label);
        await page.settle();
    }
    assert.equal(downloads(controller).length, 1);
    assert.equal(h.messages(page).filter(message => message.text === 'Already being saved for offline reading.').length, 2);
});

test('a worker gone before the next save leaves the rest unsaved', async (t) => {
    const { page, controller } = await seriesWithWorker(t);
    await page.run(`saveComicsOffline([${JSON.stringify(A)}, ${JSON.stringify(B)}])`);

    page.window.navigator.serviceWorker.controller = null;
    workerMessage(page, { type: 'OFFLINE_DOWNLOAD_COMPLETE', filePath: A, total: 2 });
    assert.equal(downloads(controller).length, 1);
    assert.equal(markText(page, B), '');
    assert.equal(page.window.sessionStorage.getItem('offlineSaveQueue'), null);
});

test('a series with no issues saves nothing, and selected series still loading are waited for', async (t) => {
    const { page, controller } = await seriesWithWorker(t);
    page.route('GET /api/files/series/s1/issues', h.seriesIssues([]));
    await page.run("saveSeriesOffline('s1')");
    assert.ok(h.messages(page).some(message => message.text === 'This series has no issues to save.'));

    await page.run(`
        selectedSeries.add('s2');
        selectedSeriesPathLoads.set('s2', Promise.resolve().then(() => {
            selectedSeriesPathLoads.delete('s2');
            selectedFiles.add(${JSON.stringify(B)});
        }));
        saveSelectedOffline()`);
    await page.settle();
    assert.deepEqual(downloads(controller).map(message => message.filePath), [B]);
});

test('a browser that refuses storage or messages still saves what it can', async (t) => {
    const controller = serviceWorkerController();
    const page = await h.bootSeriesDetail(t, {
        beforeScripts(window) {
            withController(controller)(window);
            window.sessionStorage.setItem('offlineSaveQueue', '{not json');
            Object.defineProperty(window.navigator, 'storage', {
                configurable: true,
                value: {
                    estimate: async () => { throw new Error('denied'); },
                    persist: async () => { throw new Error('denied'); }
                }
            });
        }
    });
    await page.settle();
    assert.equal(downloads(controller).length, 0, 'an unreadable stored queue is no queue');
    const stored = value => JSON.stringify(page.run(`sessionStorage.setItem('offlineSaveQueue', ${JSON.stringify(value)}); storedOfflineQueue()`));
    assert.equal(stored('{"not":"a list"}'), '[]');
    assert.equal(stored(JSON.stringify([A, 7, ''])), JSON.stringify([A]));

    page.window.Storage.prototype.setItem = () => { throw new Error('QuotaExceededError'); };
    await page.run(`saveComicsOffline([${JSON.stringify(A)}])`);
    assert.deepEqual(downloads(controller).map(message => message.filePath), [A], 'saved, though neither kept nor persisted');

    await page.run('openOfflineComicsModal()');
    assert.equal(page.$('#offlineComicsSummary').textContent, '0 comics saved on this device.');

    // A worker that cannot take the question is no answer.
    page.window.navigator.serviceWorker.controller = { postMessage() { throw new Error('DataCloneError'); } };
    assert.equal(await page.run('refreshOfflineComics()'), false);
});
