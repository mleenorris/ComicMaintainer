'use strict';

// Settings page (js/app/settings.js): maintenance actions — the database reset
// confirmation, "Clean up database now" and the About dialog.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { bootSettings, messages, lastMessage, clearMessages, callsTo, requestLog } = require('./support/settings-helpers');

const networkError = () => {
    throw new TypeError('Failed to fetch');
};

const isActive = (page, id) => page.$(`#${id}`).classList.contains('active');

test('the database reset asks for confirmation and can be cancelled', async () => {
    const page = await bootSettings();
    const since = page.fetchCalls.length;

    page.click('button[onclick="showResetConfirmation()"]');
    assert.equal(isActive(page, 'resetConfirmationModal'), true);
    page.click('#resetConfirmationModal .modal-footer button[onclick="closeResetConfirmation()"]');
    assert.equal(isActive(page, 'resetConfirmationModal'), false);

    page.click('button[onclick="showResetConfirmation()"]');
    page.click('#resetConfirmationModal .close-btn');
    assert.equal(isActive(page, 'resetConfirmationModal'), false);

    await page.settle();
    assert.deepEqual(requestLog(page, since), [], 'nothing is reset');
});

test('confirming the reset clears the library state and refreshes the views', async () => {
    const page = await bootSettings({
        routes: {
            'POST /api/settings/reset': { success: true, message: 'Database reset completed successfully' },
            'GET /api/files/counts': { total: 0, processed: 0, unprocessed: 0, duplicates: 0 }
        }
    });
    page.run("selectedFiles.add('/comics/a.cbz'); selectedSeries.add('series-1')");
    clearMessages(page);

    page.click('button[onclick="showResetConfirmation()"]');
    const since = page.fetchCalls.length;
    page.click('#resetConfirmationModal button[onclick="confirmReset()"]');
    assert.equal(isActive(page, 'resetConfirmationModal'), false, 'the dialog closes straight away');
    await page.settle();

    const reset = callsTo(page, 'POST /api/settings/reset', since);
    assert.equal(reset.length, 1);
    assert.equal(reset[0].headers['content-type'], 'application/json');
    assert.match(reset[0].headers.authorization, /^Bearer /);
    assert.deepEqual(messages(page), [
        { type: 'info', text: 'Resetting database... This may take a moment.' },
        { type: 'success', text: 'Database reset completed successfully' }
    ]);
    assert.equal(page.evaluate('selectedFiles.size'), 0);
    assert.equal(page.evaluate('selectedSeries.size'), 0);
    const log = requestLog(page, since);
    assert.ok(log.indexOf('GET /api/files/counts') > log.indexOf('POST /api/settings/reset'), 'library counts are refreshed');
    const refresh = callsTo(page, 'GET /api/files/series', since);
    assert.equal(refresh.length, 1, 'the library view is reloaded');
    assert.equal(refresh[0].query.refresh, 'true');
});

test('a completed reset is still reported when refreshing the library afterwards fails', async () => {
    const page = await bootSettings({
        routes: { 'POST /api/settings/reset': { success: true, message: 'Database reset completed successfully' } }
    });
    // Both refreshes normally handle their own errors; the reset must not rely
    // on that, so make them reject outright.
    page.run(`
        window.refreshAttempts = [];
        loadLibraryHealth = async () => { refreshAttempts.push(['health']); throw new Error('counts unavailable'); };
        loadActiveLibraryView = async (pageNumber, refresh) => { refreshAttempts.push(['view', pageNumber, refresh]); throw new Error('view unavailable'); };
    `);
    clearMessages(page);

    await page.run('confirmReset()');

    assert.deepEqual(page.evaluate('refreshAttempts'), [['health'], ['view', 1, true]], 'the view is refreshed even after the counts fail');
    assert.deepEqual(messages(page), [
        { type: 'info', text: 'Resetting database... This may take a moment.' },
        { type: 'success', text: 'Database reset completed successfully' }
    ]);
    assert.deepEqual(page.errors, []);
});

test('a failed reset is reported', async () => {
    const page = await bootSettings();
    const route = 'POST /api/settings/reset';
    const cases = [
        [respond(500, { success: false, error: 'Error resetting database: disk I/O error' }),
            'Failed to reset database: Error resetting database: disk I/O error'],
        [respond(500, {}), 'Failed to reset database: HTTP error! status: 500'],
        [{ success: false, error: 'A job is still running' }, 'A job is still running'],
        [{ success: false }, 'Failed to reset database'],
        [networkError, 'Failed to reset database: Failed to fetch']
    ];

    for (const [response, expected] of cases) {
        page.route(route, response);
        clearMessages(page);
        page.run("selectedFiles.add('/comics/kept.cbz')");
        await page.run('confirmReset()');
        assert.deepEqual(messages(page), [
            { type: 'info', text: 'Resetting database... This may take a moment.' },
            { type: 'error', text: expected }
        ]);
        assert.equal(page.evaluate('selectedFiles.size'), 1, 'the selection is kept when nothing was reset');
    }

    page.route(route, 401);
    clearMessages(page);
    await page.run('confirmReset()');
    assert.deepEqual(messages(page), [{ type: 'info', text: 'Resetting database... This may take a moment.' }]);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
});

test('Clean up database now reports how many stale entries were removed', async () => {
    const route = 'POST /api/settings/cleanup-database';
    const page = await bootSettings({
        routes: {
            [route]: { success: true, message: 'Database cleanup completed. Removed 4 stale entries.', removedCount: 4 }
        }
    });
    const button = 'button[onclick="cleanupDatabaseNow()"]';

    clearMessages(page);
    page.click(button);
    await page.settle();
    assert.deepEqual(messages(page), [
        { type: 'info', text: 'Starting database cleanup...' },
        { type: 'success', text: 'Database cleanup completed. Removed 4 stale entries.' }
    ]);
    assert.match(callsTo(page, route)[0].headers.authorization, /^Bearer /);

    const failures = [
        [{ success: false, error: 'Cleanup already running' }, 'Cleanup already running'],
        [{ success: false }, 'Failed to cleanup database'],
        [respond(500, { success: false, error: 'Error during database cleanup: locked' }),
            'Failed to cleanup database: HTTP error! status: 500'],
        [networkError, 'Failed to cleanup database: Failed to fetch']
    ];
    for (const [response, expected] of failures) {
        page.route(route, response);
        page.click(button);
        await page.settle();
        assert.deepEqual(lastMessage(page), { type: 'error', text: expected });
    }

    page.route(route, 401);
    clearMessages(page);
    page.click(button);
    await page.settle();
    assert.deepEqual(messages(page), [{ type: 'info', text: 'Starting database cleanup...' }]);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
});

test('the About dialog shows the server version', async () => {
    const page = await bootSettings({ routes: { 'GET /api/version': { version: '2.4.0', platform: 'linux-x64' } } });
    const aboutItem = '#settingsDropdownMenu button[onclick^="openAboutModal()"]';

    page.click('#settingsMenuToggle');
    assert.equal(page.$('#settingsDropdownMenu').classList.contains('show'), true);
    const since = page.fetchCalls.length;
    page.click(aboutItem);
    assert.equal(page.$('#settingsDropdownMenu').classList.contains('show'), false, 'the menu closes');
    await page.settle();

    assert.deepEqual(requestLog(page, since), ['GET /api/version']);
    assert.equal(page.$('#aboutVersion').textContent, 'v2.4.0');
    assert.equal(isActive(page, 'aboutModal'), true);

    page.click('#aboutModal .close-btn');
    assert.equal(isActive(page, 'aboutModal'), false);

    // A response without a version leaves the last known one.
    page.route('GET /api/version', { platform: 'linux-x64' });
    await page.run('openAboutModal()');
    assert.equal(page.$('#aboutVersion').textContent, 'v2.4.0');
    assert.equal(isActive(page, 'aboutModal'), true);
    page.run('closeAboutModal()');

    clearMessages(page);
    page.route('GET /api/version', respond(500, { error: 'boom' }));
    await page.run('openAboutModal()');
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Failed to load version information: HTTP error! status: 500' }]);
    assert.equal(isActive(page, 'aboutModal'), false);

    page.route('GET /api/version', networkError);
    await page.run('openAboutModal()');
    assert.deepEqual(lastMessage(page), { type: 'error', text: 'Failed to load version information: Failed to fetch' });

    clearMessages(page);
    page.route('GET /api/version', 401);
    await page.run('openAboutModal()');
    assert.deepEqual(messages(page), []);
    assert.equal(isActive(page, 'aboutModal'), false);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
});
