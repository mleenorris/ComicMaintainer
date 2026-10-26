'use strict';

// Saving a scheduled job's settings on the scheduled jobs page (#/jobs,
// js/app/scheduled-jobs.js): the request sent, the card afterwards and the
// error paths. Run now is in scheduled-jobs-run.test.js.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const h = require('./support/jobs-helpers');

const LIST = '/api/scheduled-jobs';
const SAVE = '/api/scheduled-jobs/library-scan';
const listLoads = page => h.requests(page, 'GET', LIST).length;
const openJobsPage = h.openScheduledJobsPage;

const NEXT_RUN = '2026-01-02T04:04:05';
const NEXT_RUN_SHOWN = new Date('2026-01-02T04:04:05Z').toLocaleString();
// LibraryScanJobHandler's default options.
const LIBRARY_SCAN_DEFAULTS = '{"scanForNew":true,"reconcileDeletions":true,"retagStale":true,"reprocessModified":false}';

test('Save sends the card settings and shows the job as saved', async (t) => {
    const stored = h.scheduledJob({ enabled: false, optionsJson: LIBRARY_SCAN_DEFAULTS });
    const saving = deferred();
    const { page, server } = await openJobsPage(t, [stored], { [`PUT ${SAVE}`]: () => saving.promise });

    h.editJobField(page, 'library-scan', 'enabled', true);
    h.editJobField(page, 'library-scan', 'intervalMinutes', '0360');
    h.editJobField(page, 'library-scan', 'retagStale', false);
    h.editJobField(page, 'library-scan', 'reprocessModified', true);
    h.clickJobAction(page, 'library-scan', 'save');
    await page.settle();

    const [put] = h.requests(page, 'PUT', SAVE);
    assert.equal(put.init.credentials, 'include');
    assert.match(put.headers.authorization, /^Bearer /);
    assert.deepEqual({ ...put.body, optionsJson: JSON.parse(put.body.optionsJson) }, {
        enabled: true,
        intervalMinutes: 360,
        optionsJson: { scanForNew: true, reconcileDeletions: true, retagStale: false, reprocessModified: true }
    });
    assert.equal(h.jobCardView(page, 'library-scan').saveDisabled, true, 'Save is disabled while saving');

    const saved = h.scheduledJob({ enabled: true, intervalMinutes: 360, nextRunUtc: NEXT_RUN, optionsJson: put.body.optionsJson });
    server.jobs = [saved];
    saving.resolve(saved);
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'success', text: 'Library Scan saved.' }]);
    h.assertJobCard(page, 'library-scan', {
        enabled: true,
        interval: '360',
        hint: 'Every 6 hours',
        options: { scanForNew: true, reconcileDeletions: true, retagStale: false, reprocessModified: true },
        runs: { 'Last run': 'Never', 'Next run': NEXT_RUN_SHOWN },
        saveDisabled: false
    });
    assert.equal(listLoads(page), 2, 'the status is read again after saving');
});

test('Save shows the settings the server stored', async (t) => {
    // The server answers with the saved job; the card shows that, not what was typed.
    const stored = h.scheduledJob({ enabled: true, intervalMinutes: 60, optionsJson: LIBRARY_SCAN_DEFAULTS });
    const { page } = await openJobsPage(t, [stored], {
        [`PUT ${SAVE}`]: () => ({ ...stored, enabled: false, intervalMinutes: 1440, nextRunUtc: null, optionsJson: '{"scanForNew":false}' })
    });
    h.editJobField(page, 'library-scan', 'intervalMinutes', '45');
    h.clickJobAction(page, 'library-scan', 'save');
    await page.settle();

    h.assertJobCard(page, 'library-scan', {
        enabled: false,
        interval: '1440',
        hint: 'Every day',
        options: { scanForNew: false, reconcileDeletions: true, retagStale: true, reprocessModified: false }
    });
});

test('saving a job without option checkboxes keeps its stored options', async (t) => {
    const backfill = h.scheduledJob({ jobKey: 'metadata-backfill', displayName: 'Metadata Backfill', enabled: false, intervalMinutes: 15, optionsJson: '{"maxBatch":200}' });
    // A job whose handler is gone: the server knows only its key.
    const orphan = h.scheduledJob({ jobKey: 'thumbnail-cleanup', displayName: '', description: '', intervalMinutes: 90 });
    const echo = job => call => ({ ...job, ...call.body });
    const { page } = await openJobsPage(t, [backfill, orphan], {
        'PUT /api/scheduled-jobs/metadata-backfill': echo(backfill),
        'PUT /api/scheduled-jobs/thumbnail-cleanup': echo(orphan)
    });

    h.editJobField(page, 'metadata-backfill', 'enabled', true);
    h.clickJobAction(page, 'metadata-backfill', 'save');
    await page.settle();
    h.clickJobAction(page, 'thumbnail-cleanup', 'save');
    await page.settle();

    assert.deepEqual(h.requests(page, 'PUT', '/api/scheduled-jobs/metadata-backfill').map(call => call.body),
        [{ enabled: true, intervalMinutes: 15, optionsJson: '{"maxBatch":200}' }]);
    assert.deepEqual(h.requests(page, 'PUT', '/api/scheduled-jobs/thumbnail-cleanup').map(call => call.body),
        [{ enabled: true, intervalMinutes: 90, optionsJson: null }]);
    assert.deepEqual(h.takeMessages(page), [
        { type: 'success', text: 'Metadata Backfill saved.' },
        { type: 'success', text: 'thumbnail-cleanup saved.' }
    ]);
    h.assertJobCard(page, 'metadata-backfill', { enabled: true, interval: '15' });
});

test('Save refuses an interval that is not a whole number of minutes', async (t) => {
    const { page } = await openJobsPage(t, [h.scheduledJob()], { [`PUT ${SAVE}`]: call => call.body });
    for (const typed of ['', '0', '-5', '1.5']) {
        const input = h.editJobField(page, 'library-scan', 'intervalMinutes', typed);
        input.blur();
        h.clickJobAction(page, 'library-scan', 'save');
        await page.settle();
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Enter an interval for Library Scan in whole minutes (at least 1).' }], `"${typed}"`);
        assert.equal(page.document.activeElement, input, 'the interval field is focused for correction');
    }
    assert.equal(h.requests(page, 'PUT', SAVE).length, 0);
    h.assertJobCard(page, 'library-scan', { saveDisabled: false });
});

test('a save that fails says why and keeps the edits', async (t) => {
    const failures = [
        {
            name: 'an interval the server cannot store',
            interval: '9999999999',
            answer: respond(400, {
                type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
                title: 'One or more validation errors occurred.',
                status: 400,
                errors: { '$.intervalMinutes': ['The JSON value could not be converted to System.Int32. Path: $.intervalMinutes | LineNumber: 0 | BytePositionInLine: 41.'] }
            }, { 'X-Correlation-Id': 'req-400a' }),
            text: 'Failed to save Library Scan: HTTP error! status: 400 (ref: req-400a)'
        },
        // 401/403 failures are explained by themselves and carry no reference.
        { name: 'lost administrator rights', answer: respond(403, undefined, { 'X-Correlation-Id': 'req-403a' }), text: 'Failed to save Library Scan: Only administrators can manage scheduled jobs.' },
        { name: 'a job the server no longer has', answer: respond(404, h.NOT_FOUND_PROBLEM), text: 'Failed to save Library Scan: HTTP error! status: 404' },
        { name: 'an unexpected server error', answer: h.unexpectedError(SAVE, 'req-500a'), text: `Failed to save Library Scan: ${h.UNEXPECTED_ERROR} (ref: req-500a)` },
        { name: 'a proxy error page', answer: respond(502, 'Bad Gateway'), text: 'Failed to save Library Scan: HTTP error! status: 502' },
        { name: 'a network error', answer: () => { throw new TypeError('Failed to fetch'); }, text: 'Failed to save Library Scan: Failed to fetch' }
    ];
    for (const { name, interval = '120', answer, text } of failures) {
        const { page } = await openJobsPage(t, [h.scheduledJob()], { [`PUT ${SAVE}`]: answer });
        h.editJobField(page, 'library-scan', 'enabled', false);
        h.editJobField(page, 'library-scan', 'intervalMinutes', interval);
        h.clickJobAction(page, 'library-scan', 'save');
        await page.settle();

        assert.equal(h.requests(page, 'PUT', SAVE).length, 1, name);
        assert.deepEqual(h.takeMessages(page), [{ type: 'error', text }], name);
        h.assertJobCard(page, 'library-scan', { enabled: false, interval, saveDisabled: false });
        assert.equal(listLoads(page), 1, `${name}: no status reload`);
    }
});

test('a save rejected for an expired session goes to the login page', async (t) => {
    const { page } = await openJobsPage(t, [h.scheduledJob()], { [`PUT ${SAVE}`]: 401 });
    h.clickJobAction(page, 'library-scan', 'save');
    await page.settle();
    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(h.messages(page), []);
    h.assertJobCard(page, 'library-scan', { saveDisabled: false });
});

test('Save and Run now ignore a job that is not on the page', async (t) => {
    // Reachable only by calling the page's functions directly: every card's
    // buttons name a job the page lists.
    const { page } = await openJobsPage(t, [h.scheduledJob()]);
    await page.run("saveScheduledJob('no-such-job')");
    await page.run("runScheduledJob('no-such-job')");
    await page.settle();
    assert.deepEqual(page.fetchCalls.filter(call => call.method !== 'GET').map(call => call.url), []);
    assert.deepEqual(h.messages(page), []);
    assert.deepEqual(page.errors, []);
    h.assertJobCard(page, 'library-scan', { run: 'Run now', runDisabled: false, saveDisabled: false });
});
