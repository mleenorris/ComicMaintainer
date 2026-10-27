'use strict';

// The scheduled jobs page (#/jobs, js/app/scheduled-jobs.js) in the booted
// SPA: opening it, the job cards, loading and its failures, the
// administrator-only notice and the status refresh while the page is open.
// Saving is in scheduled-jobs-save.test.js, Run now in
// scheduled-jobs-run.test.js and the metadata audit findings in
// scheduled-jobs-findings.test.js.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const h = require('./support/jobs-helpers');

const LIST = '/api/scheduled-jobs';
const listLoads = page => h.requests(page, 'GET', LIST).length;
const listText = page => page.$('#scheduledJobsList').textContent.replace(/\s+/g, ' ').trim();
const loadErrors = page => page.consoleMessages
    .filter(entry => entry.type === 'error' && entry.args[0] === 'Failed to load scheduled jobs:')
    .map(entry => entry.args[1].message);

// Run times as the API sends them (UTC DateTimes without an offset) and as the page shows them.
const LAST_RUN = '2026-01-02T03:04:05.1234567';
const NEXT_RUN = '2026-01-02T04:04:05';
const LAST_RUN_SHOWN = new Date('2026-01-02T03:04:05.123Z').toLocaleString();
const NEXT_RUN_SHOWN = new Date('2026-01-02T04:04:05Z').toLocaleString();
const NEVER = { 'Last run': 'Never', 'Next run': 'Not scheduled' };

test('the sidebar and the settings menu open the scheduled jobs page', async (t) => {
    const page = await h.bootJobsApp(t, { routes: { [`GET ${LIST}`]: [h.scheduledJob()] } });
    assert.equal(page.$('#jobsView').hidden, true);
    assert.equal(listLoads(page), 0, 'the jobs are only loaded once the page is opened');

    page.click('#sidebarJobsBtn');
    await page.settle();
    assert.equal(page.window.location.hash, '#/jobs');
    assert.equal(page.$('#jobsView').hidden, false);
    assert.equal(page.$('#topbarTitle').textContent, 'Scheduled Jobs');
    const [load] = h.requests(page, 'GET', LIST);
    assert.equal(load.init.credentials, 'include');
    assert.match(load.headers.authorization, /^Bearer /);
    assert.deepEqual(h.jobCardKeys(page), ['library-scan']);
    assert.equal(page.$('#scheduledJobsList').getAttribute('aria-busy'), 'false');
    assert.equal(page.$('#scheduledJobsAdminNotice').hidden, true);

    page.window.location.hash = '#/library';
    await page.settle();
    assert.equal(page.$('#jobsView').hidden, true);
    page.click('#scheduledJobsMenuItem');
    await page.settle();
    assert.equal(page.window.location.hash, '#/jobs');
    assert.equal(page.$('#jobsView').hidden, false);
    assert.equal(listLoads(page), 2);
});

test('every job gets a card with its icon, schedule, options and actions', async (t) => {
    const jobs = [
        h.scheduledJob({ jobKey: 'metadata-audit', displayName: 'Metadata Audit', description: 'Reports files with a missing issue number or a mismatched series.', intervalMinutes: 10080, optionsJson: '{"autoCorrect":true}' }),
        h.scheduledJob({ jobKey: 'series-language-audit', displayName: 'Series Language Audit', enabled: false, intervalMinutes: 1440, optionsJson: '{"autoCorrect":false}' }),
        h.scheduledJob({ jobKey: 'file-naming-audit', displayName: 'File Naming Audit', intervalMinutes: 1440, optionsJson: '{"forceReprocess":true,"autoCorrect":false}' }),
        h.scheduledJob({ jobKey: 'missing-issues-check', displayName: 'Missing Issues Check', intervalMinutes: 1440, optionsJson: '{"autoDownload":false}' }),
        h.scheduledJob({ optionsJson: '{"scanForNew":true,"reconcileDeletions":false,"retagStale":true,"reprocessModified":true}' }),
        h.scheduledJob({ jobKey: 'metadata-backfill', displayName: 'Metadata Backfill', intervalMinutes: 15, optionsJson: '{"maxBatch":200}' }),
        h.scheduledJob({ jobKey: 'database-backup', displayName: 'Database Backup', intervalMinutes: 1440 }),
        // A job whose handler is gone: the server knows only its key.
        h.scheduledJob({ jobKey: 'thumbnail-cleanup', displayName: '', description: '', intervalMinutes: 90 })
    ];
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: jobs } });

    assert.deepEqual(h.jobCardKeys(page), jobs.map(job => job.jobKey));
    const common = { status: 'Never run', statusClass: 'scheduled-job-status--neverrun', runs: NEVER, run: 'Run now', runDisabled: false, saveDisabled: false };
    const expected = {
        'metadata-audit': { title: 'Metadata Audit', icon: '#i-check-circle', description: jobs[0].description, enabled: true, interval: '10080', hint: 'Every 7 days', options: { autoCorrect: true }, findings: 'View findings' },
        'series-language-audit': { title: 'Series Language Audit', icon: '#i-globe', enabled: false, interval: '1440', hint: 'Every day', options: { autoCorrect: false }, findings: null },
        'file-naming-audit': { title: 'File Naming Audit', icon: '#i-pencil', enabled: true, interval: '1440', hint: 'Every day', options: { autoCorrect: false, forceReprocess: true }, findings: null },
        'missing-issues-check': { title: 'Missing Issues Check', icon: '#i-puzzle', enabled: true, interval: '1440', hint: 'Every day', options: { autoDownload: false }, findings: null },
        'library-scan': { title: 'Library Scan', icon: '#i-folder', enabled: true, interval: '60', hint: 'Every hour', options: { scanForNew: true, reconcileDeletions: false, retagStale: true, reprocessModified: true }, findings: null },
        'metadata-backfill': { title: 'Metadata Backfill', icon: '#i-tag', enabled: true, interval: '15', hint: 'Every 15 minutes', options: {}, findings: null },
        'database-backup': { title: 'Database Backup', icon: '#i-hard-drive', enabled: true, interval: '1440', hint: 'Every day', options: {}, findings: null },
        'thumbnail-cleanup': { title: 'thumbnail-cleanup', icon: '#i-clock', description: null, enabled: true, interval: '90', hint: 'Every 90 minutes', options: {}, findings: null }
    };
    for (const [jobKey, card] of Object.entries(expected)) {
        h.assertJobCard(page, jobKey, { ...common, ...card });
    }

    // Only the metadata audit has findings to show, in a panel that starts closed.
    const audit = h.jobCard(page, 'metadata-audit');
    assert.equal(audit.querySelector('[data-job-part="findings"]').hidden, true);
    assert.equal(audit.querySelector('[data-job-action="findings"]').getAttribute('aria-expanded'), 'false');
    assert.equal(page.$$('[data-job-part="findings"]').length, 1);
    // The status badge names itself to screen readers.
    assert.equal(h.jobCard(page, 'library-scan').querySelector('.scheduled-job-status').textContent, 'Last run status: Never run');
});

test('each card shows the last run status, run times and message', async (t) => {
    const cases = [
        { lastStatus: 'NeverRun', status: 'Never run', modifier: 'neverrun', runs: NEVER },
        {
            lastStatus: 'Running', lastRunUtc: LAST_RUN, nextRunUtc: NEXT_RUN,
            status: 'Running', modifier: 'running', runs: { 'Last run': LAST_RUN_SHOWN, 'Next run': NEXT_RUN_SHOWN }
        },
        {
            lastStatus: 'Success', lastRunUtc: LAST_RUN, lastDurationMs: 4200, nextRunUtc: NEXT_RUN, lastMessage: 'Scanned 120 files: 3 new, 1 removed.',
            status: 'Success', modifier: 'success',
            runs: { 'Last run': `${LAST_RUN_SHOWN} · 4 s`, 'Next run': NEXT_RUN_SHOWN, 'Last message': 'Scanned 120 files: 3 new, 1 removed.' }
        },
        {
            lastStatus: 'Failed', lastRunUtc: LAST_RUN, lastDurationMs: 750, lastMessage: 'Database is locked',
            status: 'Failed', modifier: 'failed', runs: { 'Last run': `${LAST_RUN_SHOWN} · 750 ms`, 'Next run': 'Not scheduled', 'Last message': 'Database is locked' }
        },
        {
            lastStatus: 'Skipped', lastRunUtc: LAST_RUN, lastDurationMs: 0, lastMessage: 'Skipped: previous run still in progress.',
            status: 'Skipped', modifier: 'skipped', runs: { 'Last run': `${LAST_RUN_SHOWN} · 0 ms`, 'Next run': 'Not scheduled', 'Last message': 'Skipped: previous run still in progress.' }
        },
        {
            lastStatus: 'Cancelled', lastRunUtc: LAST_RUN, lastDurationMs: 3723000, lastMessage: 'Run cancelled.',
            status: 'Cancelled', modifier: 'cancelled', runs: { 'Last run': `${LAST_RUN_SHOWN} · 1h 2m`, 'Next run': 'Not scheduled', 'Last message': 'Run cancelled.' }
        },
        // A status this page does not know yet is named but styled as never run.
        { lastStatus: 'WaitingForNetwork', status: 'Waiting for network', modifier: 'neverrun', runs: NEVER },
        { lastStatus: null, status: 'Never run', modifier: 'neverrun', runs: NEVER },
        // A run time the page cannot read is shown as a dash.
        { lastStatus: 'Success', lastRunUtc: 'yesterday', lastDurationMs: 61000, status: 'Success', modifier: 'success', runs: { 'Last run': '— · 1m 1s', 'Next run': 'Not scheduled' } }
    ];
    const jobs = cases.map(({ status, modifier, runs, ...fields }, index) =>
        h.scheduledJob({ jobKey: `job-${index}`, displayName: `Job ${index}`, ...fields }));
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: jobs } });

    cases.forEach(({ status, modifier, runs }, index) => {
        h.assertJobCard(page, `job-${index}`, { status, statusClass: `scheduled-job-status--${modifier}`, runs });
    });
});

test('the page says so when no jobs are registered', async (t) => {
    // The second answer is not a list at all, e.g. from a misconfigured proxy.
    for (const answer of [[], { jobs: [] }]) {
        const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: answer } });
        assert.equal(listText(page), 'No scheduled jobs are registered.');
        assert.equal(page.$('#scheduledJobsList').getAttribute('aria-busy'), 'false');
        assert.deepEqual(h.messages(page), []);
    }
});

test('a load that fails says why and offers Retry', async (t) => {
    const failures = [
        { name: 'a JSON error', answer: respond(500, { error: 'Database is locked' }), reason: 'Database is locked' },
        { name: 'a JSON message', answer: respond(503, { message: 'The server is starting up.' }), reason: 'The server is starting up.' },
        { name: 'problem details', answer: respond(404, { title: 'Not Found', status: 404 }), reason: 'HTTP error! status: 404' },
        { name: 'a plain-text body', answer: respond(502, 'Bad Gateway'), reason: 'HTTP error! status: 502' },
        { name: 'a network error', answer: () => { throw new TypeError('Failed to fetch'); }, reason: 'Failed to fetch' }
    ];
    for (const { name, answer, reason } of failures) {
        const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: answer } });
        assert.equal(listText(page), `Scheduled jobs could not be loaded: ${reason} Retry`, name);
        assert.deepEqual(loadErrors(page), [reason], name);
        assert.equal(page.$('#scheduledJobsList').getAttribute('aria-busy'), 'false', name);
        assert.deepEqual(h.messages(page), [], name);
    }
});

test('Retry shows the spinner until the jobs arrive', async (t) => {
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: respond(500, { error: 'Database is locked' }) } });
    const list = page.$('#scheduledJobsList');
    const pending = deferred();
    page.route(`GET ${LIST}`, () => pending.promise);

    page.click(list.querySelector('button'));
    await page.settle();
    assert.equal(listLoads(page), 2);
    assert.equal(listText(page), 'Loading scheduled jobs...');
    assert.ok(list.querySelector('.spinner'));
    assert.equal(list.getAttribute('aria-busy'), 'true');

    pending.resolve([h.scheduledJob()]);
    await page.settle();
    assert.deepEqual(h.jobCardKeys(page), ['library-scan']);
    assert.equal(list.getAttribute('aria-busy'), 'false');
});

test('the status refresh retries a load that failed', async (t) => {
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: respond(503, { message: 'The server is starting up.' }) } });
    page.route(`GET ${LIST}`, [h.scheduledJob()]);

    await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 2);
    assert.deepEqual(h.jobCardKeys(page), ['library-scan']);
});

test('opening the page again reloads it without blanking the cards', async (t) => {
    let pending = deferred();
    pending.resolve([h.scheduledJob()]);
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: () => pending.promise } });
    const list = page.$('#scheduledJobsList');
    assert.deepEqual(h.jobCardKeys(page), ['library-scan']);

    pending = deferred();
    page.click('#sidebarJobsBtn');
    await page.settle();
    assert.equal(listLoads(page), 2);
    assert.deepEqual(h.jobCardKeys(page), ['library-scan']);
    assert.equal(list.querySelector('.spinner'), null);
    assert.equal(list.getAttribute('aria-busy'), 'true');

    pending.resolve([h.scheduledJob(), h.scheduledJob({ jobKey: 'database-backup', displayName: 'Database Backup' })]);
    await page.settle();
    assert.deepEqual(h.jobCardKeys(page), ['library-scan', 'database-backup']);
    assert.equal(list.getAttribute('aria-busy'), 'false');
});

test('a 403 replaces the jobs with the administrator notice', async (t) => {
    // E.g. the user's administrator role was revoked since they signed in.
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: respond(403) } });
    const notice = page.$('#scheduledJobsAdminNotice');
    const list = page.$('#scheduledJobsList');
    assert.equal(notice.hidden, false);
    assert.equal(list.hidden, true);
    assert.equal(list.children.length, 0);
    assert.equal(list.getAttribute('aria-busy'), 'false');
    assert.deepEqual(h.messages(page), []);
    assert.deepEqual(loadErrors(page), []);

    // Nothing is refreshed behind the notice.
    await h.tick(t, page, 30000);
    await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 1);

    // Given the role back, the user opens the page again.
    page.route(`GET ${LIST}`, [h.scheduledJob()]);
    page.click('#sidebarJobsBtn');
    await page.settle();
    assert.equal(notice.hidden, true);
    assert.equal(list.hidden, false);
    assert.deepEqual(h.jobCardKeys(page), ['library-scan']);
});

test('an expired session sends the user to the login page', async (t) => {
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: 401 } });
    assert.equal(h.loginRedirects(page), 1);
    assert.doesNotMatch(listText(page), /could not be loaded/);
    assert.deepEqual(loadErrors(page), []);
    assert.deepEqual(h.messages(page), []);
});

test('non-administrators get a notice instead of the jobs and no way in', async (t) => {
    const page = await h.bootJobsApp(t, { hash: '#/jobs', user: { canAdminister: false }, routes: { [`GET ${LIST}`]: respond(403) } });
    assert.equal(page.$('#scheduledJobsAdminNotice').hidden, false);
    assert.equal(page.$('#scheduledJobsList').hidden, true);
    assert.equal(page.$('#scheduledJobsList').children.length, 0);
    for (const id of ['sidebarJobsBtn', 'scheduledJobsMenuItem', 'scheduledJobsShortcutItem']) {
        assert.equal(page.$(`#${id}`).hidden, true, `#${id} is hidden`);
    }
    assert.deepEqual(h.messages(page), []);

    // Following an old link to the page does not even ask for the jobs.
    const loads = listLoads(page);
    page.window.location.hash = '#/library';
    await page.settle();
    page.window.location.hash = '#/jobs';
    await page.settle();
    assert.equal(page.$('#jobsView').hidden, false);
    assert.equal(page.$('#scheduledJobsAdminNotice').hidden, false);
    assert.equal(page.$('#scheduledJobsList').hidden, true);
    await h.tick(t, page, 30000);
    assert.equal(listLoads(page), loads);
});

test('jobs that arrive after the user turned out not to be an administrator are not shown', async (t) => {
    for (const answer of [[h.scheduledJob()], respond(500, { error: 'Database is locked' })]) {
        const pending = deferred();
        const page = await h.bootJobsApp(t, { hash: '#/jobs', user: { canAdminister: false }, routes: { [`GET ${LIST}`]: () => pending.promise } });
        // The page asks before the user's capabilities are known.
        assert.equal(listLoads(page), 1);
        assert.equal(page.$('#scheduledJobsAdminNotice').hidden, false);

        pending.resolve(answer);
        await page.settle();
        assert.equal(page.$('#scheduledJobsList').hidden, true);
        assert.equal(page.$('#scheduledJobsList').children.length, 0);
        assert.deepEqual(loadErrors(page), []);
        await h.tick(t, page, 30000);
        assert.equal(listLoads(page), 1);
    }
});

test('the interval hint reads the typed interval in words', async (t) => {
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: [h.scheduledJob()] } });
    const invalid = 'Enter a whole number of minutes (at least 1).';
    const typed = [
        ['1440', 'Every day'], ['10080', 'Every 7 days'], ['120', 'Every 2 hours'], ['1', 'Every minute'],
        ['90', 'Every 90 minutes'], ['0', invalid], ['1.5', invalid], ['', invalid]
    ];
    for (const [value, hint] of typed) {
        h.editJobField(page, 'library-scan', 'intervalMinutes', value);
        assert.equal(h.jobCardView(page, 'library-scan').hint, hint, `"${value}"`);
    }
});

test('while open, the page refreshes job status every 30 seconds and keeps unsaved edits', async (t) => {
    const server = { jobs: [h.scheduledJob()] };
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: () => server.jobs } });
    const card = h.jobCard(page, 'library-scan');
    h.editJobField(page, 'library-scan', 'enabled', false);
    h.editJobField(page, 'library-scan', 'intervalMinutes', '15');
    h.editJobField(page, 'library-scan', 'reprocessModified', true);

    server.jobs = [h.scheduledJob({
        lastStatus: 'Success', lastRunUtc: LAST_RUN, lastDurationMs: 4200, nextRunUtc: NEXT_RUN, lastMessage: 'Scanned 120 files: 3 new, 1 removed.'
    })];
    await h.tick(t, page, 29999);
    assert.equal(listLoads(page), 1);
    await h.tick(t, page, 1);
    assert.equal(listLoads(page), 2);
    assert.equal(h.jobCard(page, 'library-scan'), card, 'the card is updated in place');
    h.assertJobCard(page, 'library-scan', {
        status: 'Success',
        runs: { 'Last run': `${LAST_RUN_SHOWN} · 4 s`, 'Next run': NEXT_RUN_SHOWN, 'Last message': 'Scanned 120 files: 3 new, 1 removed.' },
        enabled: false,
        interval: '15',
        hint: 'Every 15 minutes',
        options: { scanForNew: true, reconcileDeletions: true, retagStale: true, reprocessModified: true }
    });
    assert.equal(page.$('#scheduledJobsList').getAttribute('aria-busy'), 'false');

    // A job registered since then gets a card.
    server.jobs = [...server.jobs, h.scheduledJob({ jobKey: 'database-backup', displayName: 'Database Backup' })];
    await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 3);
    assert.deepEqual(h.jobCardKeys(page), ['library-scan', 'database-backup']);

    // A failed refresh leaves the last known status on screen and is tried again.
    page.route(`GET ${LIST}`, () => { throw new TypeError('Failed to fetch'); });
    await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 4);
    assert.deepEqual(h.jobCardKeys(page), ['library-scan', 'database-backup']);
    h.assertJobCard(page, 'library-scan', { status: 'Success' });
    const warnings = page.consoleMessages.filter(entry => entry.type === 'warn' && entry.args[0] === 'Failed to refresh scheduled jobs:');
    assert.deepEqual(warnings.map(entry => entry.args[1].message), ['Failed to fetch']);
    assert.deepEqual(loadErrors(page), []);
    assert.deepEqual(h.messages(page), []);

    page.route(`GET ${LIST}`, () => server.jobs);
    await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 5);
});

test('the status refresh waits while the tab is in the background and stops when the page is left', async (t) => {
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: [h.scheduledJob()] } });

    Object.defineProperty(page.document, 'hidden', { value: true, configurable: true });
    for (let i = 0; i < 3; i++) await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 1);

    delete page.document.hidden;
    assert.equal(page.document.hidden, false);
    await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 2);

    page.window.location.hash = '#/library';
    await page.settle();
    for (let i = 0; i < 3; i++) await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 2);
});

test('a load that finishes after the page was left schedules no refresh', async (t) => {
    const pending = deferred();
    const page = await h.bootJobsApp(t, { hash: '#/jobs', routes: { [`GET ${LIST}`]: () => pending.promise } });
    page.window.location.hash = '#/library';
    await page.settle();

    pending.resolve([h.scheduledJob()]);
    await page.settle();
    for (let i = 0; i < 3; i++) await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 1);
});

test('without its list markup the page loads nothing', async (t) => {
    const page = await h.bootJobsApp(t, { routes: { [`GET ${LIST}`]: [h.scheduledJob()] } });
    page.$('#scheduledJobsList').remove();

    page.window.location.hash = '#/jobs';
    await page.settle();
    page.run('renderScheduledJobsList()');
    await h.tick(t, page, 30000);
    assert.equal(listLoads(page), 0);
    assert.deepEqual(h.messages(page), []);
});
