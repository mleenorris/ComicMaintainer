'use strict';

// The rest of js/app/library.js: the "Scan Unmarked" action, the External
// Providers status modal, the force-reprocess prompt used by the bulk jobs
// and the legacy pagination shims kept for old callers.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const {
    makeSeriesList,
    bootLibrary,
    callsTo,
    folderCalls,
    seriesCalls,
    messages
} = require('./support/library-helpers');

const SCAN_BUTTON = '#appSidebar button[onclick="scanUnmarkedFiles()"]';
const PROVIDERS_BUTTON = '#appSidebar button[onclick="openExternalProvidersModal()"]';
const providerCalls = page => callsTo(page, '/api/metadata/providers');
const widgetText = page => page.$('#providerHealthWidget').textContent.trim();

// ── Scan Unmarked ─────────────────────────────────────────────────────────

test('Scan Unmarked reports how many files are marked and unmarked', async () => {
    const page = await bootLibrary({
        routes: { 'POST /api/scan-unmarked': { total_count: 10, unmarked_count: 3, marked_count: 7 } }
    });

    page.click(SCAN_BUTTON);
    assert.deepEqual(messages(page), [{ type: 'info', text: 'Scanning for unmarked files...' }]);
    await page.settle();

    const [call] = callsTo(page, '/api/scan-unmarked', 'POST');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.deepEqual(messages(page).at(-1), {
        type: 'success',
        text: 'Found 3 unmarked file(s) and 7 marked file(s) out of 10 total files.'
    });
});

test('Scan Unmarked from the settings menu closes the menu', async () => {
    const page = await bootLibrary({
        routes: { 'POST /api/scan-unmarked': { total_count: 0, unmarked_count: 0, marked_count: 0 } }
    });
    page.click('#settingsMenuToggle');
    assert.ok(page.$('#settingsDropdownMenu').classList.contains('show'));

    page.click('#settingsDropdownMenu button[onclick^="scanUnmarkedFiles()"]');
    await page.settle();

    assert.ok(!page.$('#settingsDropdownMenu').classList.contains('show'));
    assert.equal(messages(page).at(-1).text, 'Found 0 unmarked file(s) and 0 marked file(s) out of 0 total files.');
});

test('a failed scan is reported', async () => {
    const page = await bootLibrary({ routes: { 'POST /api/scan-unmarked': respond(500, 'Error scanning files') } });

    page.click(SCAN_BUTTON);
    await page.settle();

    assert.deepEqual(messages(page).at(-1), { type: 'error', text: 'Failed to scan files: HTTP error! status: 500' });
});

test('a scan with an expired session goes to the login page', async () => {
    const page = await bootLibrary({ routes: { 'POST /api/scan-unmarked': 401 } });

    page.click(SCAN_BUTTON);
    await page.settle();

    assert.ok(page.navigations.some(n => n.url === 'http://localhost/login.html'));
    assert.deepEqual(messages(page).filter(m => m.type === 'error'), []);
});

// ── External providers status ─────────────────────────────────────────────

const PROVIDERS = [
    { name: 'ComicVine', enabled: true, configured: true, reachable: true, status_message: 'OK', success_count: 5, failure_count: 0, last_success_utc: '2024-05-01T10:00:00Z' },
    { name: 'Metron', enabled: true, configured: true, reachable: true, status_message: '', success_count: 0, failure_count: 2, last_error: 'timeout' },
    { name: 'MangaDex', enabled: true, configured: true, reachable: false, status_message: 'Unreachable' },
    { name: 'AniList', enabled: false, configured: true, reachable: null },
    { name: 'GCD', enabled: true, configured: false, reachable: null, status_message: 'API key missing' },
    { name: 'Kitsu', enabled: true, configured: true, reachable: null }
];

test('the External Providers modal shows each provider\'s health', async () => {
    const page = await bootLibrary({ routes: { 'GET /api/metadata/providers': { providers: PROVIDERS } } });
    const modal = page.$('#externalProvidersModal');

    page.click(PROVIDERS_BUTTON);
    assert.ok(modal.classList.contains('show'));
    assert.equal(modal.style.display, 'flex');
    assert.equal(page.$('#externalProvidersModalContent #providerHealthWidget').dataset.loaded, 'false');
    await page.settle();

    const [call] = providerCalls(page);
    assert.equal(call.init.credentials, 'same-origin');
    assert.match(call.headers.authorization, /^Bearer /);

    const widget = page.$('#providerHealthWidget');
    assert.equal(widget.dataset.loaded, 'true');
    assert.equal(widget.querySelector('.provider-health-label').textContent, 'External providers:');
    const pills = page.$$('#providerHealthWidget .provider-health-pill').map(pill => ({
        name: pill.textContent.trim(),
        health: pill.className.replace('provider-health-pill provider-health-pill--', '')
    }));
    assert.deepEqual(pills, [
        { name: 'ComicVine', health: 'ok' },
        { name: 'Metron', health: 'warn' },
        { name: 'MangaDex', health: 'error' },
        { name: 'AniList', health: 'disabled' },
        { name: 'GCD', health: 'disabled' },
        { name: 'Kitsu', health: 'warn' }
    ]);

    const tooltips = page.$$('#providerHealthWidget .provider-health-pill').map(pill => pill.title.split('\n'));
    const lastSuccess = page.evaluate("new Date('2024-05-01T10:00:00Z').toLocaleString()");
    assert.deepEqual(tooltips[0], ['Enabled', 'Configured', 'OK', `Last success: ${lastSuccess}`, 'Successes: 5, Failures: 0']);
    assert.deepEqual(tooltips[1], ['Enabled', 'Configured', 'Last error: timeout', 'No successful lookups yet', 'Successes: 0, Failures: 2']);
    assert.deepEqual(tooltips[3], ['Disabled', 'Configured', 'No successful lookups yet', 'Successes: 0, Failures: 0']);
    assert.deepEqual(tooltips[4], ['Enabled', 'Not configured', 'API key missing', 'No successful lookups yet', 'Successes: 0, Failures: 0']);
});

test('the providers modal refreshes on demand and closes from its buttons', async () => {
    let providers = [PROVIDERS[0]];
    const page = await bootLibrary({ routes: { 'GET /api/metadata/providers': () => ({ providers }) } });
    page.click(PROVIDERS_BUTTON);
    await page.settle();

    providers = [PROVIDERS[2]];
    page.click('#externalProvidersModal .modal-footer button[onclick="loadProviderHealth()"]');
    await page.settle();
    assert.equal(providerCalls(page).length, 2);
    assert.deepEqual(page.$$('#providerHealthWidget .provider-health-pill').map(pill => pill.textContent.trim()), ['MangaDex']);

    page.click('#externalProvidersModal .close-btn');
    const modal = page.$('#externalProvidersModal');
    assert.ok(!modal.classList.contains('show'));
    assert.equal(modal.style.display, 'none');

    // Reopened from the settings menu, which closes the menu.
    page.click('#settingsMenuToggle');
    page.click('#settingsDropdownMenu button[onclick^="openExternalProvidersModal()"]');
    await page.settle();
    assert.ok(modal.classList.contains('show'));
    assert.ok(!page.$('#settingsDropdownMenu').classList.contains('show'));
    assert.equal(providerCalls(page).length, 3);

    page.click('#externalProvidersModal .modal-footer button[onclick="closeExternalProvidersModal()"]');
    assert.equal(modal.style.display, 'none');
});

test('the providers modal says when no provider is configured', async () => {
    for (const body of [{ providers: [] }, {}]) {
        const page = await bootLibrary({ routes: { 'GET /api/metadata/providers': body } });
        page.click(PROVIDERS_BUTTON);
        await page.settle();

        assert.equal(widgetText(page), 'No external providers are configured.');
        assert.equal(page.$('#providerHealthWidget').dataset.loaded, 'false');
        page.close();
    }
});

test('the providers modal reports a status it could not fetch', async () => {
    const page = await bootLibrary({ routes: { 'GET /api/metadata/providers': 503 } });
    page.click(PROVIDERS_BUTTON);
    await page.settle();
    assert.equal(widgetText(page), 'Unable to fetch provider status.');

    page.route('GET /api/metadata/providers', () => { throw new TypeError('Failed to fetch'); });
    page.click('#externalProvidersModal .modal-footer button[onclick="loadProviderHealth()"]');
    await page.settle();
    assert.equal(widgetText(page), 'Unable to fetch provider status.');
    const warning = page.consoleMessages.find(message => message.type === 'warn' && message.args[0] === 'Provider health fetch failed');
    assert.match(String(warning.args[1]), /Failed to fetch/);
});

test('refreshing before the modal was ever opened requests nothing', async () => {
    const page = await bootLibrary({ routes: { 'GET /api/metadata/providers': { providers: PROVIDERS } } });

    page.click('#externalProvidersModal .modal-footer button[onclick="loadProviderHealth()"]');
    await page.settle();

    assert.equal(providerCalls(page).length, 0);
    assert.equal(page.$('#providerHealthWidget'), null);
});

test('without the providers modal markup opening and closing it does nothing', async () => {
    const page = await bootLibrary({ routes: { 'GET /api/metadata/providers': { providers: PROVIDERS } } });
    page.$('#externalProvidersModalContent').remove();

    page.click(PROVIDERS_BUTTON);
    await page.settle();
    assert.equal(providerCalls(page).length, 0);
    assert.ok(!page.$('#externalProvidersModal').classList.contains('show'));

    page.$('#externalProvidersModal').remove();
    page.run('openExternalProvidersModal(); closeExternalProvidersModal()');
    assert.equal(providerCalls(page).length, 0);
    assert.deepEqual(page.errors, []);
});

test('scheduled provider-status reloads are debounced into one request', async t => {
    const page = await bootLibrary({ routes: { 'GET /api/metadata/providers': { providers: PROVIDERS } } });
    page.click(PROVIDERS_BUTTON);
    await page.settle();
    t.mock.timers.enable({ apis: ['setTimeout'] });

    page.run('scheduleProviderHealthLoad(); scheduleProviderHealthLoad()');
    t.mock.timers.tick(49);
    await page.settle();
    assert.equal(providerCalls(page).length, 1);

    t.mock.timers.tick(1);
    await page.settle();
    assert.equal(providerCalls(page).length, 2);
});

// ── Odds and ends ─────────────────────────────────────────────────────────

test('the force-reprocess prompt explains both choices and returns the answer', async () => {
    for (const confirm of [true, false]) {
        const page = await bootLibrary({ confirm });

        const answer = page.evaluate("promptForForceReprocess('This will process all files in the watched directory.', 'processed')");

        assert.equal(answer, confirm);
        assert.deepEqual(page.dialogs.at(-1), {
            kind: 'confirm',
            message: 'This will process all files in the watched directory.\n\nClick OK to include files already marked as processed, or Cancel to skip them.'
        });
        page.close();
    }
});

test('the legacy pagination entry points are harmless no-ops', async () => {
    const page = await bootLibrary({ series: makeSeriesList(2) });
    const requests = page.fetchCalls.length;
    const list = page.$('#fileList').innerHTML;

    await page.run('updatePagination(); nextPage(); previousPage(); changePerPage()');

    assert.equal(page.fetchCalls.length, requests);
    assert.equal(page.$('#fileList').innerHTML, list);
    assert.deepEqual(page.errors, []);
});

test('the legacy loadFiles() entry point loads the folder list', async () => {
    const page = await bootLibrary({ routes: { 'GET /api/files/folders': { folders: [], offset: 0, limit: 100, total_folders: 0, unmarked_count: 0 } } });

    await page.run('loadFiles(1, true)');

    assert.deepEqual(folderCalls(page).at(-1).query, { offset: '0', limit: '100', refresh: 'true' });
});

test('a pagination bar, where present, is hidden while a series page is open', async () => {
    const page = await bootLibrary({
        hash: '#/series/series-1',
        series: makeSeriesList(1),
        routes: { 'GET /api/files/series/series-1/issues': { issues: [], issue_count: 0 } }
    });
    page.$('#fileList').insertAdjacentHTML('afterend', '<div id="pagination">Page 1</div>');

    page.click('#headerFilterMenu [data-filter="marked"]');
    await page.settle();

    assert.equal(seriesCalls(page).at(-1).query.filter, 'marked');
    assert.equal(page.$('#pagination').style.display, 'none');
});
