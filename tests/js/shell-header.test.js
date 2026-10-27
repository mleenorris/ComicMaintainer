'use strict';

// js/app/shell.js — header search, theme, the signed-in user's capabilities,
// the offline banner and the active-job lookup.

const test = require('node:test');
const assert = require('node:assert/strict');
const { boot, consoleLines, preferences, requests, wentToLogin } = require('./support/core-helpers');

const DARK = '(prefers-color-scheme: dark)';
const useMockTimers = t => t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
const seriesSearches = page => requests(page, 'GET', '/api/files/series').map(call => call.query.search ?? null);
const theme = page => page.document.documentElement.getAttribute('data-theme');

/** The listener initTheme() attached for system colour-scheme changes. */
const colourSchemeQuery = page => page.mediaQueries.filter(query => query.media === DARK).at(-1);

test('typing in the header search shows the clear button and searches once typing pauses', async t => {
    useMockTimers(t);
    const page = await boot({ hash: '#/library' });
    const clear = page.$('#headerSearchClear');
    const searchesBefore = seriesSearches(page).length;
    assert.equal(clear.hidden, true);

    page.input('#headerSearchInput', 'sa');
    assert.equal(clear.hidden, false);
    t.mock.timers.tick(200);
    page.input('#headerSearchInput', 'saga');
    t.mock.timers.tick(299);
    await page.settle();
    assert.equal(seriesSearches(page).length, searchesBefore, 'still typing');

    t.mock.timers.tick(1);
    await page.settle();
    assert.deepEqual(seriesSearches(page).slice(searchesBefore), ['saga'], 'one search for the final text');
    assert.equal(page.evaluate('searchQuery'), 'saga');
});

test('the clear button empties the search, cancels a pending search and refocuses the box', async t => {
    useMockTimers(t);
    const page = await boot({ hash: '#/library' });
    const input = page.$('#headerSearchInput');

    page.input(input, 'saga');
    t.mock.timers.tick(300);
    await page.settle();
    page.input(input, 'sag');
    const searchesBefore = seriesSearches(page).length;

    page.click('#headerSearchClear');
    await page.settle();
    assert.equal(input.value, '');
    assert.equal(page.$('#headerSearchClear').hidden, true);
    assert.equal(page.document.activeElement, input);
    assert.equal(page.evaluate('searchQuery'), '');
    assert.deepEqual(seriesSearches(page).slice(searchesBefore), [null], 'the unfiltered list is reloaded at once');

    t.mock.timers.tick(1000);
    await page.settle();
    assert.equal(seriesSearches(page).length, searchesBefore + 1, 'the pending "sag" search was cancelled');

    assert.equal(page.evaluate('clearHeaderSearch()'), false, 'nothing to clear');
    assert.equal(seriesSearches(page).length, searchesBefore + 1);
});

test('the search helpers tolerate missing markup', async () => {
    const page = await boot({ hash: '#/library' });
    page.$('#headerSearchClear').remove();

    page.run('syncHeaderSearchClear()');
    page.$('#headerSearchInput').value = 'saga';
    assert.equal(page.evaluate('clearHeaderSearch()'), true);
    assert.equal(page.$('#headerSearchInput').value, '');

    page.$('#headerSearchInput').remove();
    assert.equal(page.evaluate('clearHeaderSearch()'), false);
    assert.deepEqual(page.errors, []);
});

test('a saved theme wins over the system colour scheme, even when it changes', async () => {
    const page = await boot({ routes: { 'GET /api/preferences': preferences({ theme: 'dark' }) } });
    assert.equal(theme(page), 'dark');

    colourSchemeQuery(page).change(false);
    await page.settle();
    assert.equal(theme(page), 'dark');
});

test('without a saved theme the page follows the system colour scheme as it changes', async () => {
    const page = await boot({ media: { [DARK]: true } });
    assert.equal(theme(page), 'dark');

    colourSchemeQuery(page).change(false);
    await page.settle();
    assert.equal(theme(page), 'light');

    colourSchemeQuery(page).change(true);
    await page.settle();
    assert.equal(theme(page), 'dark');

    const light = await boot();
    assert.equal(theme(light), 'light');
});

test('toggling the theme flips it and saves the choice', async () => {
    const page = await boot();
    page.route('PUT /api/preferences', undefined);
    const toggle = page.$('.sidebar-icon-btn[aria-label="Toggle theme"]');

    toggle.click();
    await page.settle();
    assert.equal(theme(page), 'dark');
    toggle.click();
    await page.settle();
    assert.equal(theme(page), 'light');

    page.document.documentElement.removeAttribute('data-theme');
    toggle.click();
    await page.settle();
    assert.equal(theme(page), 'dark', 'no theme yet counts as light');

    assert.deepEqual(requests(page, 'PUT', '/api/preferences').map(call => call.body), [
        { theme: 'dark' }, { theme: 'light' }, { theme: 'dark' }
    ]);
});

const ADMIN_ENTRY_POINTS = ['#diagnosticsMenuItem', '#sidebarDiagnosticsItem', '#scheduledJobsMenuItem', '#sidebarJobsBtn', '#scheduledJobsShortcutItem'];

function capabilityUi(page) {
    const settingsControls = page.$$('#settingsPageBody input, #settingsPageBody select, #settingsPageBody textarea, #settingsPageBody button');
    const group = predicate => {
        const controls = settingsControls.filter(predicate);
        return controls.every(control => control.disabled) ? 'disabled' : controls.every(control => !control.disabled) ? 'enabled' : 'mixed';
    };
    return {
        readOnly: page.document.body.classList.contains('user-read-only'),
        adminEntryPointsHidden: ADMIN_ENTRY_POINTS.map(selector => page.$(selector).hidden),
        readOnlyNoticeHidden: page.$('#settingsReadOnlyNotice').hidden,
        backupsHidden: page.$('#settings-backups').hidden,
        personalSettings: group(control => control.closest('[data-user-setting]')),
        serverSettings: group(control => !control.closest('[data-user-setting]') && !control.closest('#settings-backups')),
        backupControls: group(control => control.closest('#settings-backups')),
        saveButtons: ['#resetFilenameFormatBtn', '#saveSettingsBtn'].map(selector => page.$(selector).disabled)
    };
}

test('a read-only user loses the write controls and the administrator entry points', async () => {
    const page = await boot({ user: { canModifyLibrary: false, canAdminister: false } });

    assert.deepEqual(capabilityUi(page), {
        readOnly: true,
        adminEntryPointsHidden: [true, true, true, true, true],
        readOnlyNoticeHidden: false,
        backupsHidden: true,
        personalSettings: 'disabled',
        serverSettings: 'disabled',
        backupControls: 'disabled',
        saveButtons: [true, true]
    });
});

test('once settings have loaded, non-administrators can only change their personal settings', async () => {
    const page = await boot({ user: { canModifyLibrary: true, canAdminister: false } });
    page.run('settingsLoaded = true; applyCapabilities();');

    assert.deepEqual(capabilityUi(page), {
        readOnly: false,
        adminEntryPointsHidden: [true, true, true, true, true],
        readOnlyNoticeHidden: false,
        backupsHidden: true,
        personalSettings: 'enabled',
        serverSettings: 'disabled',
        backupControls: 'disabled',
        saveButtons: [true, true]
    });

    page.run('userCapabilities = { canModifyLibrary: true, canAdminister: true }; applyCapabilities();');
    assert.deepEqual(capabilityUi(page), {
        readOnly: false,
        adminEntryPointsHidden: [false, false, false, false, false],
        readOnlyNoticeHidden: true,
        backupsHidden: false,
        personalSettings: 'enabled',
        serverSettings: 'enabled',
        backupControls: 'enabled',
        saveButtons: [false, false]
    });
});

test('when the current user cannot be loaded every control stays available', async () => {
    for (const [route, logged] of [
        [500, 'Failed to load current user: 500'],
        [() => { throw new TypeError('Failed to fetch'); }, 'Error loading current user: Failed to fetch']
    ]) {
        const page = await boot({ routes: { 'GET /api/auth/user': route } });
        assert.ok(consoleLines(page, 'error').includes(logged), logged);
        assert.equal(page.document.body.classList.contains('user-read-only'), false);
        assert.deepEqual(ADMIN_ENTRY_POINTS.map(selector => page.$(selector).hidden), [false, false, false, false, false]);
        assert.deepEqual(page.evaluate('userCapabilities'), { canModifyLibrary: true, canAdminister: true });
    }

    const expired = await boot({ routes: { 'GET /api/auth/user': 401 } });
    assert.ok(wentToLogin(expired));
});

test('applying capabilities tolerates missing markup', async () => {
    const page = await boot({ user: { canAdminister: false } });
    for (const selector of [...ADMIN_ENTRY_POINTS, '#settingsReadOnlyNotice', '#settingsPageBody', '#resetFilenameFormatBtn', '#saveSettingsBtn']) {
        page.$(selector)?.remove();
    }

    page.run('applyCapabilities()');
    assert.deepEqual(page.errors, []);
});

test('the boot does not fetch the version, which only the About dialog shows', async () => {
    const page = await boot();

    // index.html has no header version label (the About dialog's #aboutVersion
    // is filled by openAboutModal(), see settings-maintenance.test.js).
    assert.equal(page.$('#appVersion'), null);
    assert.ok(page.$('#aboutModal #aboutVersion'));
    assert.equal(requests(page, 'GET', '/api/version').length, 0);
    assert.ok(!consoleLines(page, 'error').some(line => line.startsWith('Error loading version')));
    assert.deepEqual(page.errors, []);
});

function setOnline(page, online) {
    Object.defineProperty(page.window.navigator, 'onLine', { value: online, configurable: true });
    page.window.dispatchEvent(new page.window.Event(online ? 'online' : 'offline'));
}

test('going offline shows one assertive banner and coming back online removes it', async () => {
    const page = await boot();
    assert.equal(page.$('#appOfflineBanner'), null);

    setOnline(page, false);
    setOnline(page, false);
    const banners = page.$$('#appOfflineBanner');
    assert.equal(banners.length, 1);
    assert.equal(banners[0].getAttribute('role'), 'alert');
    assert.equal(banners[0].getAttribute('aria-live'), 'assertive');
    assert.equal(banners[0].className, 'app-update-banner app-offline-banner');
    assert.equal(banners[0].textContent, 'You are offline. Changes cannot be saved until the connection returns.');

    setOnline(page, true);
    assert.equal(page.$('#appOfflineBanner'), null);
    setOnline(page, true);
    assert.equal(page.$('#appOfflineBanner'), null);
});

test('a page opened while offline shows the banner straight away', async () => {
    const page = await boot({
        beforeScripts(window) {
            Object.defineProperty(window.navigator, 'onLine', { value: false, configurable: true });
        }
    });
    assert.ok(page.$('#appOfflineBanner'));
});

test('the active job lookup returns only a real job and survives failures', async () => {
    const page = await boot();
    const lookup = () => page.run('getActiveJobFromServer()').then(result => (result === null ? null : JSON.parse(JSON.stringify(result))));

    const job = { job_id: 'job-1', job_title: 'Processing 2 files...', status: 'running' };
    page.route('GET /api/active-job', job);
    assert.deepEqual(await lookup(), job);

    page.route('GET /api/active-job', { active: false });
    assert.equal(await lookup(), null);

    page.route('GET /api/active-job', 500);
    assert.equal(await lookup(), null);
    assert.ok(consoleLines(page, 'error').includes('Failed to get active job: 500'));

    page.route('GET /api/active-job', () => { throw new TypeError('Failed to fetch'); });
    assert.equal(await lookup(), null);
    assert.ok(consoleLines(page, 'error').includes('Error getting active job: Failed to fetch'));

    page.route('GET /api/active-job', 401);
    assert.equal(await lookup(), null);
    assert.ok(wentToLogin(page));
});
