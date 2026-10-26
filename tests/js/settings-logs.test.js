'use strict';

// Settings menu → View Logs (js/app/settings.js): the log file list, the log
// viewer's type/file/line-count filters, refresh, and its failure states.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { bootSettings, callsTo, requestLog } = require('./support/settings-helpers');

const networkError = () => {
    throw new TypeError('Failed to fetch');
};

const LOG_FILES = {
    files: [
        { filename: 'app20250102.log', last_modified: '2025-01-02 10:00:00', size_mb: 1.25 },
        { filename: 'app 2025 #1.log', last_modified: '2025-01-01 09:30:00', size_mb: 0.5 }
    ],
    count: 2
};

const viewer = page => ({
    options: page.$$('#logFile option').map(option => [option.value, option.textContent]),
    content: page.$('#logsContent').textContent,
    stats: page.$('#logStats').textContent,
    loading: page.$('#logsLoadingIndicator').style.display
});

test('View Logs lists the log files and shows the newest log', async () => {
    const page = await bootSettings({
        routes: {
            'GET /api/logs/files': LOG_FILES,
            'GET /api/logs': call => ({
                content: `${call.query.type} log, last ${call.query.lines} lines`,
                total_lines: 2000,
                shown_lines: Number(call.query.lines) || 2000,
                filename: call.query.filename || 'app20250102.log'
            })
        }
    });

    page.click('#settingsMenuToggle');
    let since = page.fetchCalls.length;
    page.click('#settingsDropdownMenu button[onclick^="openLogsModal()"]');
    assert.equal(page.$('#logsModal').classList.contains('active'), true);
    assert.equal(page.$('#settingsDropdownMenu').classList.contains('show'), false);
    await page.settle();

    assert.deepEqual(requestLog(page, since), ['GET /api/logs/files?type=app', 'GET /api/logs?lines=500&type=app']);
    assert.match(callsTo(page, 'GET /api/logs', since)[0].headers.authorization, /^Bearer /);
    assert.deepEqual(viewer(page), {
        options: [
            ['', 'Most Recent'],
            ['app20250102.log', 'app20250102.log (1.25 MB, 2025-01-02 10:00:00)'],
            ['app 2025 #1.log', 'app 2025 #1.log (0.5 MB, 2025-01-01 09:30:00)']
        ],
        content: 'app log, last 500 lines',
        stats: 'Showing 500 of 2000 total lines from app20250102.log',
        loading: 'none'
    });

    // Picking a file loads that file (its name URL-encoded).
    since = page.fetchCalls.length;
    page.input('#logFile', 'app 2025 #1.log');
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/logs?lines=500&type=app&filename=app%202025%20%231.log']);
    assert.equal(viewer(page).stats, 'Showing 500 of 2000 total lines from app 2025 #1.log');

    since = page.fetchCalls.length;
    page.input('#logLines', '0');
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/logs?lines=0&type=app&filename=app%202025%20%231.log']);
    assert.equal(viewer(page).stats, 'Showing 2000 of 2000 total lines from app 2025 #1.log');

    since = page.fetchCalls.length;
    page.click('#logsModal button[onclick="loadLogs()"]');
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/logs?lines=0&type=app&filename=app%202025%20%231.log']);

    // Another log type lists its own files and starts from its newest log.
    since = page.fetchCalls.length;
    page.input('#logType', 'watcher');
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/logs/files?type=watcher', 'GET /api/logs?lines=0&type=watcher']);
    assert.equal(viewer(page).content, 'watcher log, last 0 lines');

    page.click('#logsModal .close-btn');
    assert.equal(page.$('#logsModal').classList.contains('active'), false);
    page.run('openLogsModal()');
    page.click('#logsModal .modal-footer button[onclick="closeLogsModal()"]');
    assert.equal(page.$('#logsModal').classList.contains('active'), false);
    await page.settle();
});

test('the log viewer explains empty logs and failures', async () => {
    const page = await bootSettings({
        routes: {
            'GET /api/logs/files': { files: [], count: 0 },
            'GET /api/logs': { content: '', total_lines: 0, shown_lines: 0, filename: null }
        }
    });

    await page.run('openLogsModal()');
    assert.deepEqual(viewer(page), {
        options: [['', 'No log files available']],
        content: 'No logs available',
        stats: 'Showing 0 of 0 total lines',
        loading: 'none'
    });

    // A missing file list is treated like an empty one.
    page.route('GET /api/logs/files', {});
    await page.run('loadLogFiles()');
    assert.deepEqual(viewer(page).options, [['', 'No log files available']]);

    page.route('GET /api/logs', { error: 'Log directory is not readable' });
    await page.run('loadLogs()');
    assert.equal(viewer(page).content, 'Error: Log directory is not readable');
    assert.equal(viewer(page).stats, '');

    page.route('GET /api/logs', respond(500, { content: 'Error reading log file: locked', total_lines: 0 }));
    await page.run('loadLogs()');
    assert.equal(viewer(page).content, 'Failed to load logs: HTTP error! status: 500');
    assert.equal(viewer(page).loading, 'none');

    page.route('GET /api/logs', networkError);
    await page.run('loadLogs()');
    assert.equal(viewer(page).content, 'Failed to load logs: Failed to fetch');

    // While a log loads, the previous one is cleared and a spinner shown.
    const pending = deferred();
    page.route('GET /api/logs', () => pending.promise);
    const loading = page.run('loadLogs()');
    await page.settle();
    assert.deepEqual({ ...viewer(page), options: undefined }, { options: undefined, content: '', stats: '', loading: 'block' });
    pending.resolve({ content: 'fresh', total_lines: 1, shown_lines: 1, filename: 'app.log' });
    await loading;
    assert.equal(viewer(page).content, 'fresh');
    assert.equal(viewer(page).loading, 'none');

    // A file list failure leaves the log alone.
    for (const failure of [respond(500, { error: 'Error listing log files: denied' }), networkError]) {
        page.route('GET /api/logs/files', failure);
        const since = page.fetchCalls.length;
        await page.run('loadLogFiles()');
        assert.deepEqual(viewer(page).options, [['', 'Error loading files']]);
        assert.deepEqual(requestLog(page, since), ['GET /api/logs/files?type=app']);
    }
});

test('an expired session while viewing logs redirects to login', async () => {
    const page = await bootSettings({ routes: { 'GET /api/logs/files': 401, 'GET /api/logs': 401 } });

    let since = page.fetchCalls.length;
    await page.run('openLogsModal()');
    assert.deepEqual(requestLog(page, since), ['GET /api/logs/files?type=app'], 'no log is requested');
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');

    since = page.fetchCalls.length;
    page.$('#logsContent').textContent = 'previous log';
    await page.run('loadLogs()');
    assert.equal(requestLog(page, since).length, 1);
    assert.equal(page.navigations.filter(n => n.url === 'http://localhost/login.html').length, 2);
    assert.equal(viewer(page).content, '');
    assert.equal(viewer(page).loading, 'none');
});
