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

test('the page links the same-origin /manifest.json, which the CSP\'s manifest-src \'self\' allows', async () => {
    const page = await boot();
    const links = page.$$('link[rel="manifest"]');

    assert.equal(links.length, 1);
    // Not a blob: URL: those do not match 'self', and the manifest's
    // root-relative start_url, scope and icons cannot resolve against one.
    assert.equal(links[0].href, 'http://localhost/manifest.json');
    assert.equal(links[0].getAttribute('crossorigin'), 'use-credentials');
    assert.ok(![...page.objectUrls.values()].some(blob => /manifest/.test(blob.type)), 'no manifest blob is created');
});

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

// Browsers that install by hand, with no install prompt to hold back.
const AGENTS = {
    iPhone: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1',
    iPhoneChrome: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/139.0.7258.76 Mobile/15E148 Safari/604.1',
    // An iPad asking for desktop sites (the default) names itself a Mac.
    iPadDesktop: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15',
    macSafari: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15',
    macChrome: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36',
    macFirefox: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:142.0) Gecko/20100101 Firefox/142.0'
};

/** boot() as a browser identifying itself as `agent`, with `touchPoints` and `standalone` as given. */
function bootAs(agent, { touchPoints = 0, standalone, media } = {}) {
    return boot({
        media,
        beforeScripts(window) {
            Object.defineProperty(window.navigator, 'userAgent', { value: agent, configurable: true });
            Object.defineProperty(window.navigator, 'maxTouchPoints', { value: touchPoints, configurable: true });
            if (standalone !== undefined) Object.defineProperty(window.navigator, 'standalone', { value: standalone, configurable: true });
        }
    });
}

const shownSteps = page => page.$$('#installAppModal [data-install-platform]').filter(steps => !steps.hidden).map(steps => steps.dataset.installPlatform);

test('on an iPhone or iPad, Install App is offered from the start and shows how to add the app to the Home Screen', async () => {
    for (const [label, agent, touchPoints] of [
        ['iPhone Safari', AGENTS.iPhone, 5],
        ['iPhone Chrome', AGENTS.iPhoneChrome, 5],
        ['iPad asking for desktop sites', AGENTS.iPadDesktop, 5]
    ]) {
        const page = await bootAs(agent, { touchPoints });
        assert.deepEqual(installButtons(page), ['block', 'flex'], label);

        page.click('#installAppButton');
        assert.ok(page.$('#installAppModal').classList.contains('active'), label);
        assert.deepEqual(shownSteps(page), ['ios'], label);
        assert.match(page.$('#installAppModal [data-install-platform="ios"]').textContent, /Add to Home Screen/, label);
        assert.deepEqual(messages(page), [], label);

        page.run('closeInstallAppModal()');
        assert.equal(page.$('#installAppModal').classList.contains('active'), false, label);
    }
});

test('Safari on a Mac shows how to add the app to the Dock; other Mac browsers prompt or offer nothing', async () => {
    const safari = await bootAs(AGENTS.macSafari);
    assert.deepEqual(installButtons(safari), ['block', 'flex']);
    safari.click('#installAppSidebarButton');
    assert.deepEqual(shownSteps(safari), ['mac-safari']);
    assert.match(safari.$('#installAppModal [data-install-platform="mac-safari"]').textContent, /File > Add to Dock/);

    // Chrome and Edge prompt (beforeinstallprompt); Firefox cannot install at all.
    for (const agent of [AGENTS.macChrome, AGENTS.macFirefox]) {
        const page = await bootAs(agent);
        assert.deepEqual(installButtons(page), ['none', 'none'], agent);
        page.click('#installAppSidebarButton');
        assert.deepEqual(messages(page), [UNAVAILABLE], agent);
    }
});

test('once opened as the installed app there is nothing to install', async () => {
    for (const [label, options] of [
        ['from the Home Screen', { touchPoints: 5, standalone: true }],
        ['in its own window', { touchPoints: 5, media: { '(display-mode: standalone)': true } }]
    ]) {
        const page = await bootAs(AGENTS.iPhone, options);
        assert.deepEqual(installButtons(page), ['none', 'none'], label);
        page.click('#installAppSidebarButton');
        assert.deepEqual(messages(page), [UNAVAILABLE], label);
    }
});

test("a browser's own install prompt still comes first", async () => {
    const page = await bootAs(AGENTS.macSafari);
    const offer = offerInstall(page);
    page.click('#installAppSidebarButton');
    assert.equal(offer.prompts, 1);
    assert.equal(page.$('#installAppModal').classList.contains('active'), false);
});
