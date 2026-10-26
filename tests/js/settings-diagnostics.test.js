'use strict';

// Settings menu → System Diagnostics (js/app/settings.js): the snapshot panel,
// the plain-text report copied for bug reports, and the failure states.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { MB, bootSettings, messages, clearMessages, requestLog } = require('./support/settings-helpers');

const GB = 1024 * MB;

const SNAPSHOT = {
    generatedAtUtc: '2025-03-01T12:00:00.0000000Z',
    correlationId: 'corr-diag-1',
    application: {
        version: '2.4.0',
        environment: 'Production',
        startedAtUtc: '2025-02-27T08:55:55.0000000Z',
        uptimeSeconds: 2 * 86400 + 3 * 3600 + 4 * 60 + 5
    },
    runtime: {
        framework: '.NET 9.0.1',
        operatingSystem: 'Linux 6.1.0',
        architecture: 'X64',
        processorCount: 8,
        workingSetBytes: 256 * MB,
        managedHeapBytes: 64 * MB,
        threadCount: 31
    },
    health: {
        status: 'Healthy',
        totalDurationMs: 12,
        entries: [
            { name: 'database', status: 'Healthy', description: 'SQLite reachable', durationMs: 3 },
            { name: 'watcher', status: 'Healthy', description: null, durationMs: 1 }
        ]
    },
    watcher: { running: true, renameEnabled: true, normalizeEnabled: false, fileStabilityDelaySeconds: 5 },
    library: { total: 1200, processed: 1000, unprocessed: 200, duplicates: 3 },
    jobs: { total: 40, running: 1, interrupted: 2, failed: 3, lastStartedUtc: '2025-03-01T11:59:00Z' },
    storage: [
        { name: 'Watched', path: '/watched_dir', exists: true, writable: true, freeBytes: 10 * GB, totalBytes: 20 * GB },
        { name: 'Duplicates', path: null, exists: false, writable: false },
        { name: 'Config', path: '/Config', exists: true, writable: false, error: 'Directory could not be inspected.' }
    ],
    logs: {
        directory: '/Config/Log',
        files: [
            { name: 'app20250301.log', sizeBytes: 2048, lastWriteUtc: '2025-03-01T11:58:00Z' },
            { name: 'watcher.log', sizeBytes: 512, lastWriteUtc: '2025-03-01T11:57:00Z' }
        ]
    }
};

/** The panel as [heading, [[label, value], ...]] per section. */
const panel = page => page.$$('#diagnosticsContent .diagnostics-section').map(section => [
    section.querySelector('h3').textContent,
    [...section.querySelectorAll('dt')].map(dt => [dt.textContent, dt.nextElementSibling.textContent])
]);

const panelText = page => page.$('#diagnosticsContent').textContent.trim();

async function copyReport(page) {
    const before = page.clipboard.length;
    clearMessages(page);
    page.click('#copyDiagnosticsBtn');
    await page.settle();
    return page.clipboard.length > before ? page.clipboard.at(-1) : null;
}

test('System Diagnostics shows the snapshot and copies it as a text report', async () => {
    const page = await bootSettings({ routes: { 'GET /api/diagnostics': SNAPSHOT } });

    page.click('#settingsMenuToggle');
    assert.equal(page.$('#diagnosticsMenuItem').hidden, false);
    let since = page.fetchCalls.length;
    page.click('#diagnosticsMenuItem');
    assert.equal(page.$('#diagnosticsModal').classList.contains('active'), true);
    assert.equal(page.$('#settingsDropdownMenu').classList.contains('show'), false);
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/diagnostics']);

    assert.deepEqual(panel(page), [
        ['Application', [['Version', '2.4.0'], ['Environment', 'Production'], ['Uptime', '2d 3h 4m'], ['Health', 'Healthy']]],
        ['Health checks', [['database', 'Healthy — SQLite reachable'], ['watcher', 'Healthy']]],
        ['Runtime', [
            ['Framework', '.NET 9.0.1'], ['Operating system', 'Linux 6.1.0'], ['Architecture', 'X64'],
            ['Processors', '8'], ['Memory in use', '256.0 MB']
        ]],
        ['Watcher', [['Running', 'Yes'], ['Rename on arrival', 'Enabled'], ['Normalize on arrival', 'Disabled']]],
        ['Library', [['Files tracked', '1200'], ['Processed', '1000'], ['Unprocessed', '200'], ['Duplicates', '3']]],
        ['Jobs', [['Running or queued', '1'], ['Interrupted', '2'], ['Failed', '3']]],
        ['Storage', [
            ['Watched', '/watched_dir — present, writable, 10.0 GB free'],
            ['Duplicates', 'not configured — missing, not writable'],
            ['Config', '/Config — present, not writable']
        ]],
        ['Logs', [['app20250301.log', '2.0 KB'], ['watcher.log', '512 B']]]
    ]);
    assert.equal(page.$('#diagnosticsContent .diagnostics-status').className, 'diagnostics-status ok');

    assert.equal(await copyReport(page), [
        'ComicMaintainer diagnostics',
        'Generated: 2025-03-01T12:00:00.0000000Z',
        'Reference: corr-diag-1',
        '',
        'Version: 2.4.0 (Production)',
        'Uptime: 2d 3h 4m',
        'Health: Healthy',
        '  database: Healthy — SQLite reachable',
        '  watcher: Healthy',
        'Runtime: .NET 9.0.1 on Linux 6.1.0 (X64, 8 cpu)',
        'Memory: 256.0 MB',
        'Watcher: running (rename on, normalize off)',
        'Library: 1200 files, 1000 processed, 200 unprocessed, 3 duplicates',
        'Jobs: 40 total, 1 active, 2 interrupted, 3 failed, last started 2025-03-01T11:59:00Z',
        'Storage/Watched: /watched_dir — present, writable, 10.0 GB free',
        'Storage/Duplicates: not configured — missing, not writable',
        'Storage/Config: /Config — present, not writable, Directory could not be inspected.',
        'Logs: /Config/Log',
        '  app20250301.log: 2.0 KB, last written 2025-03-01T11:58:00Z',
        '  watcher.log: 512 B, last written 2025-03-01T11:57:00Z'
    ].join('\n'));
    assert.deepEqual(messages(page), [{ type: 'success', text: 'Diagnostics report copied to the clipboard.' }]);

    // Refresh fetches a new snapshot.
    since = page.fetchCalls.length;
    page.route('GET /api/diagnostics', { ...SNAPSHOT, application: { ...SNAPSHOT.application, uptimeSeconds: 3 * 3600 + 25 * 60 } });
    page.click('#diagnosticsModal button[onclick="loadDiagnostics()"]');
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/diagnostics']);
    assert.deepEqual(panel(page)[0][1][2], ['Uptime', '3h 25m']);

    page.click('#diagnosticsModal .close-btn');
    assert.equal(page.$('#diagnosticsModal').classList.contains('active'), false);

    // The settings sidebar has its own entry point.
    page.click('#sidebarDiagnosticsItem');
    assert.equal(page.$('#diagnosticsModal').classList.contains('active'), true);
    await page.settle();
    page.click('#diagnosticsModal .modal-footer button[onclick="closeDiagnosticsModal()"]');
    assert.equal(page.$('#diagnosticsModal').classList.contains('active'), false);
});

test('partial snapshots show what the server could not determine', async () => {
    const degraded = {
        application: { uptimeSeconds: 59 },
        health: { status: 'Degraded', entries: [] },
        runtime: {},
        watcher: { running: null, error: 'Watcher status unavailable.' },
        library: { error: 'Library counts unavailable.' },
        jobs: { error: 'Job summary unavailable.' },
        storage: 'not a list',
        logs: { directory: '/Config', files: [], error: 'Log files could not be listed.' }
    };
    const page = await bootSettings({ routes: { 'GET /api/diagnostics': degraded } });

    await page.run('openDiagnosticsModal()');
    assert.deepEqual(panel(page), [
        ['Application', [['Version', 'Unknown'], ['Environment', 'Unknown'], ['Uptime', '0m'], ['Health', 'Degraded']]],
        ['Runtime', [
            ['Framework', 'Unknown'], ['Operating system', 'Unknown'], ['Architecture', 'Unknown'],
            ['Processors', 'Unknown'], ['Memory in use', 'Unknown']
        ]],
        ['Watcher', [['Status', 'Watcher status unavailable.']]],
        ['Library', [['Status', 'Library counts unavailable.']]],
        ['Jobs', [['Status', 'Job summary unavailable.']]],
        ['Storage', []],
        ['Logs', [['Directory', '/Config']]]
    ]);
    assert.equal(page.$('#diagnosticsContent .diagnostics-status').className, 'diagnostics-status warn');
    assert.equal(await copyReport(page), [
        'ComicMaintainer diagnostics',
        'Generated: unknown',
        'Reference: unknown',
        '',
        'Version: unknown (unknown)',
        'Uptime: 0m',
        'Health: Degraded',
        'Runtime: unknown on unknown (unknown, ? cpu)',
        'Memory: unknown',
        'Watcher: Watcher status unavailable.',
        'Library: Library counts unavailable.',
        'Jobs: Job summary unavailable.',
        'Logs: Log files could not be listed.'
    ].join('\n'));

    // An empty snapshot falls back to defaults everywhere.
    page.route('GET /api/diagnostics', {});
    await page.run('loadDiagnostics()');
    assert.deepEqual(panel(page), [
        ['Application', [['Version', 'Unknown'], ['Environment', 'Unknown'], ['Uptime', 'Unknown'], ['Health', 'Unknown']]],
        ['Runtime', [
            ['Framework', 'Unknown'], ['Operating system', 'Unknown'], ['Architecture', 'Unknown'],
            ['Processors', 'Unknown'], ['Memory in use', 'Unknown']
        ]],
        ['Watcher', [['Running', 'No'], ['Rename on arrival', 'Disabled'], ['Normalize on arrival', 'Disabled']]],
        ['Library', [['Files tracked', '0'], ['Processed', '0'], ['Unprocessed', '0'], ['Duplicates', '0']]],
        ['Jobs', [['Running or queued', '0'], ['Interrupted', '0'], ['Failed', '0']]],
        ['Storage', []],
        ['Logs', [['Directory', 'Unknown']]]
    ]);
    assert.equal(page.$('#diagnosticsContent .diagnostics-status').className, 'diagnostics-status bad');
    assert.equal(await copyReport(page), [
        'ComicMaintainer diagnostics',
        'Generated: unknown',
        'Reference: unknown',
        '',
        'Version: unknown (unknown)',
        'Uptime: Unknown',
        'Health: unknown',
        'Runtime: unknown on unknown (unknown, ? cpu)',
        'Memory: unknown',
        'Watcher: stopped (rename off, normalize off)',
        'Library: ? files, ? processed, ? unprocessed, ? duplicates',
        'Jobs: ? total, ? active, ? interrupted, ? failed, last started never',
        'Logs: unknown'
    ].join('\n'));

    // Each watcher switch is reported on its own.
    page.route('GET /api/diagnostics', { watcher: { running: false, renameEnabled: false, normalizeEnabled: true } });
    await page.run('loadDiagnostics()');
    assert.deepEqual(panel(page).find(([title]) => title === 'Watcher'),
        ['Watcher', [['Running', 'No'], ['Rename on arrival', 'Disabled'], ['Normalize on arrival', 'Enabled']]]);
    assert.match(await copyReport(page), /\nWatcher: stopped \(rename off, normalize on\)\n/);

    // The report tolerates log entries without a size or timestamp.
    page.route('GET /api/diagnostics', { health: { status: 'Unhealthy' }, logs: { files: [{ name: 'odd.log' }] } });
    await page.run('loadDiagnostics()');
    assert.equal(page.$('#diagnosticsContent .diagnostics-status').className, 'diagnostics-status bad');
    const report = await copyReport(page);
    assert.match(report, /\nHealth: Unhealthy\n/);
    assert.match(report, /\nLogs: unknown\n {2}odd\.log: \?, last written unknown$/);
});

test('uptime is shown in the largest sensible units', async () => {
    const page = await bootSettings();
    const cases = [
        [0, '0m'], [59, '0m'], [3599, '59m'], [3600, '1h 0m'], [86399, '23h 59m'],
        [86400, '1d 0h 0m'], [90061, '1d 1h 1m'],
        [-1, 'Unknown'], [NaN, 'Unknown'], [Infinity, 'Unknown'], ['3600', 'Unknown'], [null, 'Unknown']
    ];
    for (const [seconds, expected] of cases) {
        assert.equal(page.run(`formatUptime(${typeof seconds === 'string' ? `'${seconds}'` : seconds})`), expected, String(seconds));
    }
    assert.equal(page.run('buildDiagnosticsReport(null)'), '');
});

test('diagnostics failures are explained in the panel', async () => {
    const page = await bootSettings({ routes: { 'GET /api/diagnostics': 403 } });

    await page.run('openDiagnosticsModal()');
    assert.equal(panelText(page), 'System diagnostics are available to administrators only.');
    assert.equal(await copyReport(page), null);
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Diagnostics have not loaded yet.' }]);

    // A server failure carries its correlation id so it can be found in the logs.
    page.route('GET /api/diagnostics', respond(500, { error: 'boom' }, { 'X-Correlation-Id': 'corr-500' }));
    await page.run('loadDiagnostics()');
    assert.equal(panelText(page), 'Could not load diagnostics: HTTP error! status: 500 (ref: corr-500)');

    page.run('lastApiFailure = null');
    page.route('GET /api/diagnostics', 502);
    await page.run('loadDiagnostics()');
    assert.equal(panelText(page), 'Could not load diagnostics: HTTP error! status: 502');

    page.route('GET /api/diagnostics', () => { throw new TypeError('Failed to fetch'); });
    await page.run('loadDiagnostics()');
    assert.equal(panelText(page), 'Could not load diagnostics: Failed to fetch');

    // A failed reload forgets the previous snapshot rather than copying stale data.
    page.route('GET /api/diagnostics', SNAPSHOT);
    await page.run('loadDiagnostics()');
    page.route('GET /api/diagnostics', 503);
    await page.run('loadDiagnostics()');
    assert.equal(await copyReport(page), null);
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Diagnostics have not loaded yet.' }]);

    // While loading, the panel says so.
    const pending = deferred();
    page.route('GET /api/diagnostics', () => pending.promise);
    const loading = page.run('loadDiagnostics()');
    await page.settle();
    assert.equal(panelText(page), 'Loading diagnostics...');
    pending.resolve(SNAPSHOT);
    await loading;
    assert.equal(panel(page)[0][0], 'Application');

    page.route('GET /api/diagnostics', 401);
    await page.run('loadDiagnostics()');
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
});

test('copying falls back to manual selection when the clipboard is unavailable', async () => {
    const page = await bootSettings({ routes: { 'GET /api/diagnostics': SNAPSHOT } });
    await page.run('openDiagnosticsModal()');
    const failure = [{ type: 'error', text: 'Could not copy automatically — select the text in the panel and copy it manually.' }];
    const setClipboard = value => Object.defineProperty(page.window.navigator, 'clipboard', { value, configurable: true });

    const writes = [];
    setClipboard({
        writeText: async text => {
            writes.push(text);
            throw new page.window.DOMException('Write permission denied.', 'NotAllowedError');
        }
    });
    assert.equal(await copyReport(page), null);
    assert.equal(writes.length, 1);
    assert.match(writes[0], /^ComicMaintainer diagnostics\n/);
    assert.deepEqual(messages(page), failure);

    // Insecure (plain http) origins have no clipboard API at all.
    for (const clipboard of [undefined, {}]) {
        setClipboard(clipboard);
        assert.equal(await copyReport(page), null);
        assert.deepEqual(messages(page), failure);
    }
});

test('diagnostics tolerate a page without the diagnostics panel', async () => {
    const page = await bootSettings({ routes: { 'GET /api/diagnostics': SNAPSHOT } });

    // The snapshot is still kept for copying when the panel disappears mid-load.
    const pending = deferred();
    page.route('GET /api/diagnostics', () => pending.promise);
    const loading = page.run('loadDiagnostics()');
    await page.settle();
    page.$('#diagnosticsContent').remove();
    pending.resolve(SNAPSHOT);
    await loading;
    assert.match(await copyReport(page), /^ComicMaintainer diagnostics\nGenerated: 2025-03-01T12:00:00.0000000Z\n/);

    const since = page.fetchCalls.length;
    await page.run('loadDiagnostics()');
    page.$('#diagnosticsModal').remove();
    await page.run('openDiagnosticsModal()');
    page.run('closeDiagnosticsModal()');
    assert.deepEqual(requestLog(page, since), [], 'nothing is fetched without a panel to show it in');
    assert.deepEqual(page.errors, []);
});

test('the diagnostics entry points are hidden from non-administrators', async () => {
    const page = await bootSettings({ user: { canModifyLibrary: false, canAdminister: false }, routes: { 'GET /api/backups': 403 } });
    assert.equal(page.$('#diagnosticsMenuItem').hidden, true);
    assert.equal(page.$('#sidebarDiagnosticsItem').hidden, true);
});
