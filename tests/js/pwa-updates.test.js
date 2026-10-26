'use strict';

// js/app/service-worker.js, page side: registering /sw.js, checking for a new
// version, the "new version available" banner, and the single reload onto the
// new worker. sw.js itself is tested separately.

const test = require('node:test');
const assert = require('node:assert/strict');
const { boot, loadApp, consoleLines, requests } = require('./support/core-helpers');

/** A ServiceWorker stand-in: records postMessage() and fires 'statechange'. */
function fakeWorker(state = 'installing') {
    const listeners = [];
    return {
        state,
        posted: [],
        postMessage(message) { this.posted.push({ ...message }); },
        addEventListener(type, listener) { if (type === 'statechange') listeners.push(listener); },
        becomes(next) {
            this.state = next;
            for (const listener of [...listeners]) listener({ type: 'statechange', target: this });
        }
    };
}

/** A ServiceWorkerRegistration stand-in. found(worker) fires 'updatefound'. */
function fakeRegistration({ waiting = null, update = () => Promise.resolve() } = {}) {
    const listeners = [];
    const registration = {
        scope: 'http://localhost/',
        installing: null,
        waiting,
        active: fakeWorker('activated'),
        updates: 0,
        update() {
            registration.updates++;
            return update();
        },
        addEventListener(type, listener) { if (type === 'updatefound') listeners.push(listener); },
        found(worker) {
            registration.installing = worker;
            for (const listener of [...listeners]) listener({ type: 'updatefound' });
        }
    };
    return registration;
}

/**
 * Install `registration` as what navigator.serviceWorker.register() resolves
 * to (or `rejects` with). `controlled`: whether a worker already controls the
 * page (false on the very first visit, or after a hard refresh).
 */
function withWorker({ registration, controlled = true, rejects }) {
    return (window, page) => {
        const serviceWorker = window.navigator.serviceWorker;
        serviceWorker.controller = controlled ? fakeWorker('activated') : null;
        serviceWorker.register = async (url, init) => {
            page.serviceWorkerRegistrations.push({ url, options: { ...init } });
            if (rejects) throw rejects;
            return registration;
        };
    };
}

async function bootWithWorker({ registration = fakeRegistration(), controlled, rejects, ...options } = {}) {
    const page = await boot({ ...options, beforeScripts: withWorker({ registration, controlled, rejects }) });
    return { page, registration };
}

const banner = page => page.$('#appUpdateBanner');
const reloads = page => page.navigations.filter(navigation => navigation.reload);
const logged = (page, line) => consoleLines(page, 'log').includes(line);
const visibilityChange = page => page.document.dispatchEvent(new page.window.Event('visibilitychange'));

/** A new worker is found, installs, and is announced. */
function newVersionInstalled(registration) {
    const worker = fakeWorker('installing');
    registration.found(worker);
    worker.becomes('installed');
    return worker;
}

test('/sw.js is registered for the whole app once the page has loaded', async () => {
    const registration = fakeRegistration();
    const page = loadApp({ beforeScripts: withWorker({ registration }) });
    assert.deepEqual(page.serviceWorkerRegistrations, [], 'not while the page is still loading');

    await page.settle();

    assert.deepEqual(page.serviceWorkerRegistrations, [{ url: '/sw.js', options: { scope: '/' } }]);
    assert.ok(logged(page, 'PWA: Service Worker registered successfully: http://localhost/'));
    assert.equal(banner(page), null);
    assert.equal(registration.updates, 0, 'no update check at start-up');
});

test('without service worker support nothing is registered and the app works as usual', async () => {
    const page = await boot({ serviceWorker: false });
    assert.equal('serviceWorker' in page.window.navigator, false);
    assert.deepEqual(page.serviceWorkerRegistrations, []);
    assert.deepEqual(page.errors, []);
    assert.equal(page.evaluate('currentView'), 'home');
    assert.equal(page.eventSources.length, 1);
});

test('a failed registration is logged and leaves the page alone', async () => {
    const { page } = await bootWithWorker({ rejects: new Error('The operation is insecure.') });
    assert.ok(logged(page, 'PWA: Service Worker registration failed: The operation is insecure.'));
    assert.equal(banner(page), null);
    assert.deepEqual(page.errors, []);
    assert.deepEqual(requests(page, 'POST', '/api/client-errors'), []);
});

test('checks for a new version once a minute, only while the tab is visible', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const { page, registration } = await bootWithWorker();
    let hidden = false;
    Object.defineProperty(page.document, 'hidden', { configurable: true, get: () => hidden });
    page.run('{ let offset = 0; const start = Date.now(); Date.now = () => start + offset; globalThis.advanceClock = ms => { offset += ms; }; }');
    const minutePasses = () => {
        page.run('advanceClock(60000)');
        t.mock.timers.tick(60000);
    };

    minutePasses();
    assert.equal(registration.updates, 1);
    minutePasses();
    assert.equal(registration.updates, 2);

    // Switching away and straight back does not add a request.
    hidden = true;
    visibilityChange(page);
    hidden = false;
    visibilityChange(page);
    assert.equal(registration.updates, 2);

    // A hidden tab has nobody to show the banner to.
    hidden = true;
    visibilityChange(page);
    minutePasses();
    minutePasses();
    assert.equal(registration.updates, 2);

    // Coming back after more than a minute checks straight away.
    hidden = false;
    visibilityChange(page);
    assert.equal(registration.updates, 3);
});

test('a failed update check is only logged at debug level', async () => {
    const registration = fakeRegistration({ update: () => Promise.reject(new Error('Failed to fetch')) });
    const { page } = await bootWithWorker({ registration });

    visibilityChange(page);
    await page.settle();

    assert.equal(registration.updates, 1);
    assert.ok(consoleLines(page, 'debug').includes('PWA: Service Worker update check failed: Failed to fetch'));
    assert.deepEqual(page.errors, []);
    assert.deepEqual(requests(page, 'POST', '/api/client-errors'), []);
});

test('a version that finished installing on an earlier visit is announced straight away', async () => {
    const waiting = fakeWorker('installed');
    const { page } = await bootWithWorker({ registration: fakeRegistration({ waiting }) });

    const shown = banner(page);
    assert.ok(shown);
    assert.equal(shown.className, 'app-update-banner');
    assert.equal(shown.getAttribute('role'), 'status');
    assert.equal(shown.getAttribute('aria-live'), 'polite');
    assert.equal(shown.querySelector('.app-update-banner-text').textContent, 'A new version of Comic Maintainer is available.');
    const [reload, dismiss] = shown.querySelectorAll('button');
    assert.equal(reload.textContent, 'Reload');
    assert.equal(reload.type, 'button');
    assert.equal(dismiss.getAttribute('aria-label'), 'Dismiss update notification');
    assert.equal(dismiss.textContent, '\u00d7');
    assert.deepEqual(waiting.posted, [], 'nothing is activated until the user asks');
    assert.deepEqual(reloads(page), []);
});

test('a waiting version is not announced on a page no worker controls', async () => {
    const { page } = await bootWithWorker({ registration: fakeRegistration({ waiting: fakeWorker('installed') }), controlled: false });
    assert.equal(banner(page), null);
});

test('a new version found while the app is open is announced once it has installed, and only once', async () => {
    const { page, registration } = await bootWithWorker();
    const worker = fakeWorker('installing');

    registration.found(worker);
    assert.ok(logged(page, 'PWA: New service worker installing...'));
    worker.becomes('installing');
    assert.equal(banner(page), null, 'not while it is still installing');

    worker.becomes('installed');
    assert.ok(banner(page));
    assert.ok(logged(page, 'PWA: New version installed, prompting user to reload'));

    registration.found(worker);
    worker.becomes('installed');
    assert.equal(page.$$('.app-update-banner').length, 1);
});

test('the very first install is not announced: there is no older version to replace', async () => {
    const { page, registration } = await bootWithWorker({ controlled: false });
    newVersionInstalled(registration);
    assert.equal(banner(page), null);
    assert.ok(logged(page, 'PWA: Service worker installed for the first time'));
});

test('an update notice without an installing worker is ignored', async () => {
    const { page, registration } = await bootWithWorker();
    registration.found(null);
    assert.equal(banner(page), null);
    assert.deepEqual(page.errors, []);
});

test('an even newer version replaces the banner, and Reload applies that one', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const { page, registration } = await bootWithWorker();
    const first = newVersionInstalled(registration);
    const second = newVersionInstalled(registration);

    assert.equal(page.$$('.app-update-banner').length, 1);
    page.click('#appUpdateBanner .btn-primary');

    assert.deepEqual(second.posted, [{ type: 'SKIP_WAITING' }]);
    assert.deepEqual(first.posted, []);
});

test('Reload asks the new worker to take over and reloads once it is in control', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const waiting = fakeWorker('installed');
    const { page } = await bootWithWorker({ registration: fakeRegistration({ waiting }) });
    const stream = page.eventSources.at(-1);

    const reload = page.click('#appUpdateBanner .btn-primary');

    assert.equal(reload.disabled, true);
    assert.equal(reload.textContent, 'Reloading…');
    assert.deepEqual(waiting.posted, [{ type: 'SKIP_WAITING' }]);
    assert.equal(stream.readyState, 2, 'the event stream is closed so the old worker can go idle');
    assert.deepEqual(reloads(page), []);

    waiting.becomes('activating');
    assert.deepEqual(reloads(page), [], 'not in control yet');

    page.window.navigator.serviceWorker.emit('controllerchange');
    assert.deepEqual(reloads(page), [{ url: 'http://localhost/index.html#/home', reload: true }]);
    assert.ok(logged(page, 'PWA: New service worker took control, reloading page...'));

    waiting.becomes('activated');
    page.window.navigator.serviceWorker.emit('controllerchange');
    t.mock.timers.tick(5000);
    assert.equal(reloads(page).length, 1, 'the page reloads only once');
});

test('Reload also follows the new worker becoming activated or redundant', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    for (const state of ['activated', 'redundant']) {
        const waiting = fakeWorker('installed');
        const { page } = await bootWithWorker({ registration: fakeRegistration({ waiting }) });
        page.click('#appUpdateBanner .btn-primary');

        waiting.becomes(state);

        assert.equal(reloads(page).length, 1, state);
        assert.ok(logged(page, `PWA: Service worker is ${state}, reloading page...`), state);
        page.window.navigator.serviceWorker.emit('controllerchange');
        t.mock.timers.tick(5000);
        assert.equal(reloads(page).length, 1, state);
    }
});

test('Reload gives up waiting for the new worker after 5 seconds and reloads anyway', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const waiting = fakeWorker('installed');
    const { page } = await bootWithWorker({ registration: fakeRegistration({ waiting }) });
    page.click('#appUpdateBanner .btn-primary');

    t.mock.timers.tick(4999);
    assert.deepEqual(reloads(page), []);
    t.mock.timers.tick(1);

    assert.equal(reloads(page).length, 1);
    assert.ok(logged(page, 'PWA: New service worker did not take control in time, reloading page...'));
});

test('Reload with a worker that is already activated or redundant reloads at once, without SKIP_WAITING', async () => {
    for (const state of ['activated', 'redundant']) {
        const waiting = fakeWorker('installed');
        const { page } = await bootWithWorker({ registration: fakeRegistration({ waiting }) });
        waiting.state = state;

        page.click('#appUpdateBanner .btn-primary');

        assert.equal(reloads(page).length, 1, state);
        assert.ok(logged(page, `PWA: Service worker is ${state}, reloading page...`), state);
        assert.deepEqual(waiting.posted, [], state);
    }
});

test('a dismissed version is not announced again, but a newer one is', async () => {
    const { page, registration } = await bootWithWorker();
    const worker = newVersionInstalled(registration);

    page.click('#appUpdateBanner .app-update-banner-dismiss');
    assert.equal(banner(page), null);
    assert.deepEqual(worker.posted, []);
    assert.deepEqual(reloads(page), []);

    registration.found(worker);
    worker.becomes('installed');
    assert.equal(banner(page), null, 'already announced');

    newVersionInstalled(registration);
    assert.ok(banner(page));
});

test('a new version taking control from another tab reloads this one too, once', async () => {
    const { page } = await bootWithWorker();
    page.window.navigator.serviceWorker.emit('controllerchange');
    page.window.navigator.serviceWorker.emit('controllerchange');
    assert.equal(reloads(page).length, 1);
});
