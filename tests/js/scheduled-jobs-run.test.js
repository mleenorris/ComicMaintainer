'use strict';

// Run now on the scheduled jobs page (#/jobs, js/app/scheduled-jobs.js):
// starting a run, following it until it finishes, announcing how it went and
// the error paths. Saving is in scheduled-jobs-save.test.js.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const h = require('./support/jobs-helpers');

const LIST = '/api/scheduled-jobs';
const RUN = '/api/scheduled-jobs/library-scan/run';
const listLoads = page => h.requests(page, 'GET', LIST).length;
const openJobsPage = h.openScheduledJobsPage;

const ACCEPTED = respond(202, { jobKey: 'library-scan', queued: true });

// Run times as the API sends them (UTC DateTimes without an offset) and as the page shows them.
const PREVIOUS_RUN = '2026-01-01T03:00:00';
const LAST_RUN = '2026-01-02T03:04:05.1234567';
const NEXT_RUN = '2026-01-02T04:04:05';
const LAST_RUN_SHOWN = new Date('2026-01-02T03:04:05.123Z').toLocaleString();
const NEXT_RUN_SHOWN = new Date('2026-01-02T04:04:05Z').toLocaleString();

test('Run now starts the job and follows it until it finishes', async (t) => {
    const before = h.scheduledJob({
        lastStatus: 'Success', lastRunUtc: PREVIOUS_RUN, lastDurationMs: 4000, nextRunUtc: NEXT_RUN, lastMessage: 'Scanned 118 files.'
    });
    const accepting = deferred();
    const { page, server } = await openJobsPage(t, [before], { [`POST ${RUN}`]: () => accepting.promise });

    h.clickJobAction(page, 'library-scan', 'run');
    await page.settle();
    const [post] = h.requests(page, 'POST', RUN);
    assert.equal(post.init.credentials, 'include');
    assert.match(post.headers.authorization, /^Bearer /);
    h.assertJobCard(page, 'library-scan', { run: 'Starting…', runDisabled: true });

    server.jobs = [{ ...before, lastStatus: 'Running' }];
    accepting.resolve(ACCEPTED);
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Library Scan started.' }]);
    assert.equal(listLoads(page), 2, 'the status is read as soon as the run is accepted');
    h.assertJobCard(page, 'library-scan', { status: 'Running', run: 'Running…', runDisabled: true });

    // Checked every 2 seconds while it runs.
    await h.tick(t, page, 1999);
    assert.equal(listLoads(page), 2);
    await h.tick(t, page, 1);
    assert.equal(listLoads(page), 3);
    h.assertJobCard(page, 'library-scan', { status: 'Running', run: 'Running…', runDisabled: true });
    assert.deepEqual(h.messages(page), []);

    server.jobs = [{ ...before, lastStatus: 'Success', lastRunUtc: LAST_RUN, lastDurationMs: 61000, lastMessage: 'Scanned 120 files: 3 new, 1 removed.' }];
    await h.tick(t, page, 2000);
    assert.equal(listLoads(page), 4);
    assert.deepEqual(h.takeMessages(page), [{ type: 'success', text: 'Library Scan finished: Scanned 120 files: 3 new, 1 removed.' }]);
    h.assertJobCard(page, 'library-scan', {
        status: 'Success',
        runs: { 'Last run': `${LAST_RUN_SHOWN} · 1m 1s`, 'Next run': NEXT_RUN_SHOWN, 'Last message': 'Scanned 120 files: 3 new, 1 removed.' },
        run: 'Run now',
        runDisabled: false
    });

    // Back to the slow refresh, with nothing more to say.
    await h.tick(t, page, 2000);
    assert.equal(listLoads(page), 4);
    await h.tick(t, page, 28000);
    assert.equal(listLoads(page), 5);
    assert.deepEqual(h.messages(page), []);
});

test('how a followed run ended is announced', async (t) => {
    const outcomes = [
        { lastStatus: 'Success', lastMessage: 'Scanned 120 files: 3 new, 1 removed.', toast: { type: 'success', text: 'Library Scan finished: Scanned 120 files: 3 new, 1 removed.' } },
        { lastStatus: 'Success', lastMessage: null, toast: { type: 'success', text: 'Library Scan finished.' } },
        { lastStatus: 'Failed', lastMessage: 'Database is locked', toast: { type: 'error', text: 'Library Scan failed: Database is locked' } },
        { lastStatus: 'Failed', lastMessage: null, toast: { type: 'error', text: 'Library Scan failed.' } },
        { lastStatus: 'Cancelled', lastMessage: 'Run cancelled.', toast: { type: 'info', text: 'Library Scan: Cancelled: Run cancelled.' } },
        { lastStatus: 'Cancelled', lastMessage: null, toast: { type: 'info', text: 'Library Scan: Cancelled.' } }
    ];
    for (const { lastStatus, lastMessage, toast } of outcomes) {
        const { page, server } = await openJobsPage(t, [h.scheduledJob()], { [`POST ${RUN}`]: ACCEPTED });
        server.jobs = [h.scheduledJob({ lastStatus: 'Running' })];
        h.clickJobAction(page, 'library-scan', 'run');
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Library Scan started.' }]);

        server.jobs = [h.scheduledJob({ lastStatus, lastRunUtc: LAST_RUN, lastDurationMs: 1500, lastMessage })];
        await h.tick(t, page, 2000);
        assert.deepEqual(h.takeMessages(page), [toast], `${lastStatus} ${lastMessage}`);
        h.assertJobCard(page, 'library-scan', { status: lastStatus, run: 'Run now', runDisabled: false });
    }
});

test('a job known only by its key is announced by its key', async (t) => {
    const orphan = { jobKey: 'thumbnail-cleanup', displayName: '', description: '' };
    const { page, server } = await openJobsPage(t, [h.scheduledJob(orphan)], {
        'POST /api/scheduled-jobs/thumbnail-cleanup/run': respond(202, { jobKey: 'thumbnail-cleanup', queued: true })
    });
    server.jobs = [h.scheduledJob({ ...orphan, lastStatus: 'Success', lastRunUtc: LAST_RUN, lastDurationMs: 20 })];
    h.clickJobAction(page, 'thumbnail-cleanup', 'run');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [
        { type: 'info', text: 'thumbnail-cleanup started.' },
        { type: 'success', text: 'thumbnail-cleanup finished.' }
    ]);
});

test('a run that finished before the first status check is announced too', async (t) => {
    const cases = [
        {
            name: 'after an earlier run',
            before: { lastStatus: 'Success', lastRunUtc: PREVIOUS_RUN, lastDurationMs: 4000 },
            after: { lastStatus: 'Success', lastRunUtc: LAST_RUN, lastDurationMs: 900, lastMessage: 'Scanned 120 files: nothing new.' },
            toast: { type: 'success', text: 'Library Scan finished: Scanned 120 files: nothing new.' }
        },
        {
            name: 'on its first run',
            before: {},
            after: { lastStatus: 'Failed', lastRunUtc: LAST_RUN, lastDurationMs: 900, lastMessage: 'Watched directory not found.' },
            toast: { type: 'error', text: 'Library Scan failed: Watched directory not found.' }
        }
    ];
    for (const { name, before, after, toast } of cases) {
        const { page, server } = await openJobsPage(t, [h.scheduledJob(before)], { [`POST ${RUN}`]: ACCEPTED });
        server.jobs = [h.scheduledJob(after)];
        h.clickJobAction(page, 'library-scan', 'run');
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Library Scan started.' }, toast], name);
        h.assertJobCard(page, 'library-scan', { run: 'Run now', runDisabled: false });
    }
});

test('a status check still showing the previous run keeps following', async (t) => {
    const before = h.scheduledJob({ lastStatus: 'Success', lastRunUtc: PREVIOUS_RUN, lastDurationMs: 4000, lastMessage: 'Scanned 118 files.' });
    const { page, server } = await openJobsPage(t, [before], { [`POST ${RUN}`]: ACCEPTED });
    h.clickJobAction(page, 'library-scan', 'run');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Library Scan started.' }]);
    h.assertJobCard(page, 'library-scan', { status: 'Success', run: 'Starting…', runDisabled: true });

    // The same instant, written with a fraction, is still the previous run.
    server.jobs = [{ ...before, lastRunUtc: `${PREVIOUS_RUN}.0000000` }];
    await h.tick(t, page, 2000);
    assert.equal(listLoads(page), 3);
    assert.deepEqual(h.messages(page), []);
    h.assertJobCard(page, 'library-scan', { run: 'Starting…', runDisabled: true });

    server.jobs = [{ ...before, lastRunUtc: LAST_RUN, lastMessage: 'Scanned 120 files.' }];
    await h.tick(t, page, 2000);
    assert.deepEqual(h.takeMessages(page), [{ type: 'success', text: 'Library Scan finished: Scanned 120 files.' }]);
    h.assertJobCard(page, 'library-scan', { run: 'Run now', runDisabled: false });
});

test('a run that never starts is no longer followed after 30 seconds', async (t) => {
    // E.g. the server dropped it because a timer run of the job was in progress.
    const before = h.scheduledJob({ lastStatus: 'Success', lastRunUtc: PREVIOUS_RUN, lastDurationMs: 4000 });
    const { page } = await openJobsPage(t, [before], { [`POST ${RUN}`]: ACCEPTED });
    const clock = h.pageClock(t, page);
    h.clickJobAction(page, 'library-scan', 'run');
    await page.settle();
    h.takeMessages(page);

    for (let i = 0; i < 15; i++) await clock.advance(2000);
    assert.equal(listLoads(page), 17);
    h.assertJobCard(page, 'library-scan', { run: 'Starting…', runDisabled: true });

    await clock.advance(2000);
    assert.equal(listLoads(page), 18);
    h.assertJobCard(page, 'library-scan', { status: 'Success', run: 'Run now', runDisabled: false });
    assert.deepEqual(h.messages(page), []);

    await clock.advance(2000);
    assert.equal(listLoads(page), 18, 'back to the slow refresh');
    await clock.advance(28000);
    assert.equal(listLoads(page), 19);
});

test('a Run now request that fails says why and re-enables the button', async (t) => {
    const failures = [
        {
            name: 'a job the server no longer has',
            answer: respond(404, h.NOT_FOUND_PROBLEM, { 'X-Correlation-Id': 'req-404b' }),
            text: 'Failed to start Library Scan: HTTP error! status: 404 (ref: req-404b)'
        },
        // 401/403 failures are explained by themselves and carry no reference.
        { name: 'lost administrator rights', answer: respond(403, undefined, { 'X-Correlation-Id': 'req-403b' }), text: 'Failed to start Library Scan: Only administrators can manage scheduled jobs.' },
        { name: 'an unexpected server error', answer: h.unexpectedError(RUN, 'req-500b'), text: `Failed to start Library Scan: ${h.UNEXPECTED_ERROR} (ref: req-500b)` },
        { name: 'a network error', answer: () => { throw new TypeError('Failed to fetch'); }, text: 'Failed to start Library Scan: Failed to fetch' }
    ];
    for (const { name, answer, text } of failures) {
        const { page } = await openJobsPage(t, [h.scheduledJob()], { [`POST ${RUN}`]: answer });
        h.clickJobAction(page, 'library-scan', 'run');
        await page.settle();

        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text }], name);
        h.assertJobCard(page, 'library-scan', { run: 'Run now', runDisabled: false });
        assert.equal(listLoads(page), 1, `${name}: no status reload`);
        await h.tick(t, page, 2000);
        assert.equal(listLoads(page), 1, `${name}: nothing is followed`);
    }
});

test('a Run now request rejected for an expired session goes to the login page', async (t) => {
    const { page } = await openJobsPage(t, [h.scheduledJob()], { [`POST ${RUN}`]: 401 });
    h.clickJobAction(page, 'library-scan', 'run');
    await page.settle();
    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(h.messages(page), []);
    h.assertJobCard(page, 'library-scan', { run: 'Run now', runDisabled: false });
});

test('leaving the page stops following a run', async (t) => {
    const { page, server } = await openJobsPage(t, [h.scheduledJob()], { [`POST ${RUN}`]: ACCEPTED });
    server.jobs = [h.scheduledJob({ lastStatus: 'Running' })];
    h.clickJobAction(page, 'library-scan', 'run');
    await page.settle();
    h.assertJobCard(page, 'library-scan', { run: 'Running…', runDisabled: true });

    page.window.location.hash = '#/library';
    await page.settle();
    const loads = listLoads(page);
    for (let i = 0; i < 3; i++) await h.tick(t, page, 2000);
    assert.equal(listLoads(page), loads, 'no status checks while away');

    // Back on the page the run is still reported as running, but only a run
    // followed from the page blocks Run now (the server itself skips a run
    // requested while one is in progress).
    page.window.location.hash = '#/jobs';
    await page.settle();
    assert.equal(listLoads(page), loads + 1);
    h.assertJobCard(page, 'library-scan', { status: 'Running', run: 'Run now', runDisabled: false });
    await h.tick(t, page, 2000);
    assert.equal(listLoads(page), loads + 1, 'the slow refresh again');
});

test('a job that disappears while its Run now request is pending', async (t) => {
    // E.g. the server was upgraded to a version without the job meanwhile.
    const backup = h.scheduledJob({ jobKey: 'database-backup', displayName: 'Database Backup' });
    const answering = deferred();
    const { page, server } = await openJobsPage(t, [h.scheduledJob(), backup], {
        'POST /api/scheduled-jobs/database-backup/run': () => answering.promise
    });
    h.clickJobAction(page, 'database-backup', 'run');
    await page.settle();

    server.jobs = [h.scheduledJob()];
    await h.tick(t, page, 30000);
    assert.deepEqual(h.jobCardKeys(page), ['library-scan']);

    answering.resolve(respond(404, h.NOT_FOUND_PROBLEM));
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to start Database Backup: HTTP error! status: 404' }]);
    assert.deepEqual(h.jobCardKeys(page), ['library-scan']);
    assert.deepEqual(page.errors, []);
});
