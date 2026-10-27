'use strict';

// js/app/service-worker.js: the PWA install prompt. The browser's
// beforeinstallprompt is held back, both "Install App" buttons are shown, and
// clicking one replays the browser's prompt.

const test = require('node:test');
const assert = require('node:assert/strict');
const { boot, consoleLines, messages } = require('./support/core-helpers');

const UNAVAILABLE = { type: 'info', text: 'App is already installed or installation is not available.', role: null };

/** Fire beforeinstallprompt; the user will answer the replayed prompt with `outcome`. */
function offerInstall(page, outcome = 'accepted') {
    const event = new page.window.Event('beforeinstallprompt', { cancelable: true });
    const offer = { event, prompts: 0 };
    event.prompt = () => { offer.prompts++; };
    event.userChoice = Promise.resolve({ outcome, platform: 'web' });
    page.window.dispatchEvent(event);
    return offer;
}

const installButtons = page => ['#installAppButton', '#installAppSidebarButton'].map(selector => page.$(selector).style.display);
const logged = (page, line) => consoleLines(page, 'log').includes(line);

test('the browser\'s install prompt is held back and both Install App buttons are shown instead', async () => {
    const page = await boot();
    assert.deepEqual(installButtons(page), ['none', 'none']);

    const offer = offerInstall(page);

    assert.equal(offer.event.defaultPrevented, true);
    assert.deepEqual(installButtons(page), ['block', 'flex']);
    assert.ok(logged(page, 'PWA: beforeinstallprompt event fired'));
});

test('Install App replays the prompt, which can only be used once whatever the answer', async () => {
    for (const [outcome, line] of [
        ['accepted', 'PWA: User accepted the install prompt'],
        ['dismissed', 'PWA: User dismissed the install prompt']
    ]) {
        const page = await boot();
        const offer = offerInstall(page, outcome);

        page.click('#installAppSidebarButton');
        assert.equal(offer.prompts, 1, outcome);
        await page.settle();
        assert.ok(logged(page, line), outcome);
        assert.deepEqual(messages(page), [], outcome);

        page.click('#installAppSidebarButton');
        assert.equal(offer.prompts, 1, `${outcome}: not prompted again`);
        assert.deepEqual(messages(page), [UNAVAILABLE], outcome);
    }
});

test('the menu\'s Install App entry prompts and closes the menu', async () => {
    const page = await boot();
    const offer = offerInstall(page);
    page.click('#settingsMenuToggle');
    assert.ok(page.$('#settingsDropdownMenu').classList.contains('show'));

    page.click('#installAppButton');

    assert.equal(offer.prompts, 1);
    assert.equal(page.$('#settingsDropdownMenu').classList.contains('show'), false);
});

test('once the app is installed the buttons are hidden and the held prompt is dropped', async () => {
    const page = await boot();
    const offer = offerInstall(page);

    page.window.dispatchEvent(new page.window.Event('appinstalled'));

    assert.deepEqual(installButtons(page), ['none', 'none']);
    assert.ok(logged(page, 'PWA: App successfully installed'));
    page.run('installApp()');
    assert.equal(offer.prompts, 0);
    assert.deepEqual(messages(page), [UNAVAILABLE]);
});

test('Install App without an offer from the browser explains that installing is unavailable', async () => {
    const page = await boot();
    page.click('#installAppSidebarButton');
    assert.deepEqual(messages(page), [UNAVAILABLE]);
});

test('a missing install button does not stop the other from being shown and hidden', async () => {
    const page = await boot();
    page.$('#installAppButton').remove();

    offerInstall(page);
    assert.equal(page.$('#installAppSidebarButton').style.display, 'flex');

    page.window.dispatchEvent(new page.window.Event('appinstalled'));
    assert.equal(page.$('#installAppSidebarButton').style.display, 'none');
    assert.deepEqual(page.errors, []);
});
