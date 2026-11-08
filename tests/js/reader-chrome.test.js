'use strict';

// The comic reader's chrome (js/pages/reader.js): the header and footer that
// get out of the way while reading, fullscreen, the way back to the library,
// and toast messages.

const test = require('node:test');
const assert = require('node:assert/strict');
const { openReader, openWebcomicReader, press, toasts, streamImage, FILE } = require('./support/reader-helpers');

const chromeHidden = page => page.$('#readerContainer').classList.contains('chrome-hidden');
const hintShowing = page => page.$('#chromeHint').classList.contains('show-once');
const activity = (page, type) => page.document.dispatchEvent(new page.window.Event(type));

/**
 * A finger tapping `element` the way a phone delivers it: touchstart and a
 * touch pointerdown, then the mouse events browsers emulate for a tap
 * (mousemove included) and finally the click.
 */
function tap(page, element) {
    const { window } = page;
    element.dispatchEvent(new window.Event('touchstart', { bubbles: true }));
    const pointerdown = new window.Event('pointerdown', { bubbles: true });
    Object.defineProperty(pointerdown, 'pointerType', { value: 'touch' });
    element.dispatchEvent(pointerdown);
    element.dispatchEvent(new window.Event('mousemove', { bubbles: true }));
    element.click();
}

/** beforeScripts hook giving jsdom a Fullscreen API; `state.requests` records the elements asked to go fullscreen. */
function fullscreenApi(state, { refuse = false } = {}) {
    return window => {
        state.element = null;
        state.requests = [];
        window.Element.prototype.requestFullscreen = function () {
            state.requests.push(this.id);
            if (refuse) return Promise.reject(new TypeError('Permissions check failed'));
            state.element = this;
            return Promise.resolve();
        };
        window.document.exitFullscreen = () => {
            state.element = null;
            return Promise.resolve();
        };
        Object.defineProperty(window.document, 'fullscreenElement', { configurable: true, get: () => state.element });
    };
}

test('the controls hide after three idle seconds and come back on any activity', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader();

    t.mock.timers.tick(2999);
    assert.equal(chromeHidden(page), false);
    t.mock.timers.tick(1);
    assert.equal(chromeHidden(page), true);
    // The "move mouse or tap" hint is shown the first time only, then fades.
    assert.equal(hintShowing(page), true);
    t.mock.timers.tick(2500);
    assert.equal(hintShowing(page), false);

    for (const type of ['mousemove', 'touchstart', 'pointerdown']) {
        activity(page, type);
        assert.equal(chromeHidden(page), false, type);
        t.mock.timers.tick(3000);
        assert.equal(chromeHidden(page), true, type);
    }
    press(page, 'Shift');
    assert.equal(chromeHidden(page), false);

    // Activity keeps pushing the deadline back.
    t.mock.timers.tick(2000);
    activity(page, 'mousemove');
    t.mock.timers.tick(2000);
    assert.equal(chromeHidden(page), false);
    t.mock.timers.tick(1000);
    assert.equal(chromeHidden(page), true);

    // Keyboard focus landing on one of the controls shows them too.
    page.$('#fullscreenBtn').focus();
    assert.equal(chromeHidden(page), false);
});

test('the controls stay up while a dialog is open', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader({ routes: { 'GET /api/email/status': { configured: true }, 'GET /api/email/devices': { devices: [] } } });

    press(page, '?');
    t.mock.timers.tick(5000);
    assert.equal(chromeHidden(page), false);
    press(page, 'Escape');
    t.mock.timers.tick(3000);
    assert.equal(chromeHidden(page), true);

    press(page, 'g');
    t.mock.timers.tick(5000);
    assert.equal(chromeHidden(page), false);
    press(page, 'Escape');
    page.$('#fullscreenBtn').focus();
    t.mock.timers.tick(3000);
    assert.equal(chromeHidden(page), true);

    activity(page, 'mousemove');
    page.click('#emailComicBtn');
    t.mock.timers.tick(5000);
    assert.equal(chromeHidden(page), false);
});

test('tapping the middle of the page toggles the controls', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader();

    tap(page, page.$('#tapZoneMiddle'));
    assert.equal(chromeHidden(page), true);
    // Hidden by hand, they stay hidden.
    t.mock.timers.tick(10000);
    assert.equal(chromeHidden(page), true);

    // From hidden, a tap brings them back (the tap itself does not count as
    // activity that shows them first and then toggles them away again).
    tap(page, page.$('#tapZoneMiddle'));
    assert.equal(chromeHidden(page), false);
    t.mock.timers.tick(3000);
    assert.equal(chromeHidden(page), true);

    // Hidden after idling, a tap brings them back too.
    tap(page, page.$('#tapZoneMiddle'));
    assert.equal(chromeHidden(page), false);
});

test('turning pages, by tapping or with the keys, leaves hidden controls hidden', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader();
    const currentPage = () => page.run('currentPage');
    t.mock.timers.tick(3000);
    assert.equal(chromeHidden(page), true);

    tap(page, page.$('#tapZoneRight'));
    await page.waitFor(() => currentPage() === 2);
    assert.equal(chromeHidden(page), true, 'a tap to turn the page');
    tap(page, page.$('#tapZoneLeft'));
    await page.waitFor(() => currentPage() === 1);
    for (const key of ['ArrowRight', 'ArrowLeft', ' ', 'd', 'a', 'PageDown', 'ArrowDown']) {
        press(page, key);
        assert.equal(chromeHidden(page), true, key);
    }
    // Pressing anything else is asking for the controls.
    press(page, 'Tab');
    assert.equal(chromeHidden(page), false);
});

test('touching the controls themselves keeps them up', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader();
    t.mock.timers.tick(2000);

    tap(page, page.$('#readerFooter'));
    t.mock.timers.tick(2000);
    assert.equal(chromeHidden(page), false, 'the tap restarted the countdown');
    t.mock.timers.tick(1000);
    assert.equal(chromeHidden(page), true);
});

test('in webcomic mode a tap on the stream toggles the controls, and scrolling leaves them alone', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openWebcomicReader({ series: { [FILE]: 3 } });
    const image = streamImage(page, FILE, 1);
    t.mock.timers.tick(3000);
    assert.equal(chromeHidden(page), true);

    // A finger scrolling the stream touches it without tapping.
    image.dispatchEvent(new page.window.Event('touchstart', { bubbles: true }));
    page.$('#readerContent').dispatchEvent(new page.window.Event('scroll'));
    assert.equal(chromeHidden(page), true);

    tap(page, image);
    assert.equal(chromeHidden(page), false);
    tap(page, image);
    assert.equal(chromeHidden(page), true);
});

test('in manga mode a click on the page outside the tap zones does not toggle the controls', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader();
    // On a phone the footer shows the page number, so the header's is dropped.
    assert.equal(page.$('#pageInfo').classList.contains('hide-on-mobile'), true);

    page.$('#readerContent').click();
    assert.equal(chromeHidden(page), false);
    t.mock.timers.tick(3000);
    page.$('#readerContent').click();
    assert.equal(chromeHidden(page), true);
});

test('the fullscreen button and F toggle fullscreen; Escape leaves fullscreen before the reader', async () => {
    const fullscreen = {};
    const page = await openReader({ beforeScripts: fullscreenApi(fullscreen) });
    const container = page.$('#readerContainer');
    const icon = page.$('#fullscreenIcon');

    page.click('#fullscreenBtn');
    await page.waitFor(() => container.classList.contains('fullscreen-mode'));
    assert.deepEqual(fullscreen.requests, ['readerContainer']);
    assert.equal(icon.textContent, '⮽');
    // A change event while still fullscreen changes nothing.
    page.document.dispatchEvent(new page.window.Event('fullscreenchange'));
    assert.ok(container.classList.contains('fullscreen-mode'));

    assert.equal(press(page, 'f').defaultPrevented, true);
    await page.waitFor(() => !container.classList.contains('fullscreen-mode'));
    assert.equal(fullscreen.element, null);
    assert.equal(icon.textContent, '⛶');

    press(page, 'F');
    await page.waitFor(() => container.classList.contains('fullscreen-mode'));

    // Escape asks the browser to leave fullscreen, which then reports the change.
    press(page, 'Escape');
    assert.equal(fullscreen.element, null);
    assert.deepEqual(page.navigations, []);
    page.document.dispatchEvent(new page.window.Event('fullscreenchange'));
    assert.equal(container.classList.contains('fullscreen-mode'), false);
    assert.equal(icon.textContent, '⛶');
});

test('a refused fullscreen request is logged and changes nothing', async () => {
    const fullscreen = {};
    const page = await openReader({ beforeScripts: fullscreenApi(fullscreen, { refuse: true }) });

    page.click('#fullscreenBtn');
    await page.waitFor(() => page.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error entering fullscreen:'));
    assert.equal(page.$('#readerContainer').classList.contains('fullscreen-mode'), false);
    assert.equal(page.$('#fullscreenIcon').textContent, '⛶');
});

test('Back returns to the library the best way available', async () => {
    const page = await openReader();
    let backs = 0;
    page.window.history.back = () => { backs++; };
    const setReferrer = url => Object.defineProperty(page.document, 'referrer', { configurable: true, value: url });
    const setReturnState = raw => page.window.sessionStorage.setItem('comicReaderReturnState', raw);
    const back = () => {
        const before = page.navigations.length;
        page.click('button[onclick="goBack()"]');
        return page.navigations.slice(before).map(navigation => navigation.url);
    };

    // Straight from the library: the browser's own Back restores it best.
    for (const referrer of ['http://localhost/', 'http://localhost/index.html', 'http://localhost/app/index.html']) {
        setReferrer(referrer);
        assert.deepEqual(back(), [], referrer);
    }
    assert.equal(backs, 3);

    // Otherwise the library page saved where it was.
    setReferrer('http://localhost/settings.html');
    setReturnState(JSON.stringify({ originUrl: '/?view=series&page=2#Batman' }));
    assert.deepEqual(back(), ['http://localhost/?view=series&page=2#Batman']);

    // Anything else falls back to the library's front page.
    setReferrer('https://elsewhere.example/');
    setReturnState(JSON.stringify({ originUrl: 'https://elsewhere.example/phish' }));
    assert.deepEqual(back(), ['http://localhost/']);
    setReturnState(JSON.stringify({ originUrl: 'http://' }));
    assert.deepEqual(back(), ['http://localhost/']);
    setReturnState('{not json');
    assert.deepEqual(back(), ['http://localhost/']);
    setReferrer('not a url');
    setReturnState(JSON.stringify({}));
    assert.deepEqual(back(), ['http://localhost/']);
    assert.equal(backs, 3);

    // Escape (outside fullscreen) is Back too.
    setReferrer('');
    page.$('#fullscreenBtn').focus();
    press(page, 'Escape');
    assert.equal(page.navigations.at(-1).url, 'http://localhost/');
});

test('toasts appear, then fade out and are removed', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader({ serviceWorker: false });

    page.click('#offlineBtn');
    const toast = page.$('#toastContainer .toast');
    assert.equal(toast.textContent, 'Offline downloads are not available in this browser');
    assert.deepEqual([...toast.classList], ['toast', 'error']);

    t.mock.timers.tick(10);
    assert.ok(toast.classList.contains('show'));
    t.mock.timers.tick(2989);
    assert.ok(toast.classList.contains('show'));
    t.mock.timers.tick(1);
    assert.equal(toast.classList.contains('show'), false);
    assert.ok(toast.isConnected);
    t.mock.timers.tick(300);
    assert.equal(toast.isConnected, false);
    assert.deepEqual(toasts(page), []);
});
