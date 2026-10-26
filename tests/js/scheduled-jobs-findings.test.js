'use strict';

// The metadata audit's findings on the scheduled jobs page (#/jobs,
// js/app/scheduled-jobs.js): opening and closing the findings panel, what it
// shows, its failures, and reloading it when an audit run started from the
// page finishes.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const h = require('./support/jobs-helpers');

const LIST = '/api/scheduled-jobs';
const FINDINGS = '/api/scheduled-jobs/metadata-audit/findings';
const RUN = '/api/scheduled-jobs/metadata-audit/run';
const findingsLoads = page => h.requests(page, 'GET', FINDINGS);
const loadErrors = page => page.consoleMessages
    .filter(entry => entry.type === 'error' && entry.args[0] === 'Failed to load metadata audit findings:')
    .map(entry => entry.args[1].message);

const AUDIT_DESCRIPTION = "Scans every tracked comic file and reports any with a missing chapter/issue number or a series tag that doesn't match the expected series name.";
const audit = (overrides = {}) => h.scheduledJob({
    jobKey: 'metadata-audit',
    displayName: 'Metadata Audit',
    description: AUDIT_DESCRIPTION,
    enabled: false,
    intervalMinutes: 10080,
    ...overrides
});

// Findings as MetadataAuditJobHandler records them and the API lists them (ordered by path).
const finding = (id, filePath, findingType, fields = {}) => ({
    id, filePath, findingType, expectedSeries: null, actualSeries: null, actualIssue: null, details: null, detectedAtUtc: '2026-01-02T03:04:05.1234567', ...fields
});
const FINDINGS_LIST = [
    finding(7, '/watched/Batman/Batman 001.cbz', 'SeriesMismatch', {
        expectedSeries: 'Batman', actualSeries: 'Batman (2016)', actualIssue: '1', details: "Series 'Batman (2016)' does not match expected 'Batman'."
    }),
    finding(8, '/watched/Saga/Saga 054.cbz', 'MissingChapter', { actualSeries: 'Saga', details: 'ComicInfo <Number> tag is empty.' }),
    finding(9, '/watched/Saga/Saga 054.cbz', 'DiskDriftedFromDb', {
        expectedSeries: 'Saga (2012)', actualSeries: 'Saga', details: 'Disk metadata diverges from user-edited DB record; backfill will rewrite.'
    }),
    finding(10, '/watched/Saga/Saga 055.cbz', 'Unreadable', { details: 'Metadata could not be read.' })
];
const FINDINGS_ROWS = [
    ['Series mismatch', '/watched/Batman/Batman 001.cbz', 'Batman', 'Batman (2016)', "Series 'Batman (2016)' does not match expected 'Batman'."],
    ['Missing chapter', '/watched/Saga/Saga 054.cbz', '', 'Saga', 'ComicInfo <Number> tag is empty.'],
    ['Disk drifted from db', '/watched/Saga/Saga 054.cbz', 'Saga (2012)', 'Saga', 'Disk metadata diverges from user-edited DB record; backfill will rewrite.'],
    ['Unreadable', '/watched/Saga/Saga 055.cbz', '', '', 'Metadata could not be read.']
];
const findingsPage = (findings, total = findings.length) => ({ total, offset: 0, limit: 200, findings });

// The findings panel and its toggle button as plain data.
function findingsView(page) {
    const card = h.jobCard(page, 'metadata-audit');
    const panel = card.querySelector('[data-job-part="findings"]');
    const button = card.querySelector('[data-job-action="findings"]');
    const table = panel.querySelector('table');
    return {
        button: button.textContent.trim(),
        expanded: button.getAttribute('aria-expanded'),
        hidden: panel.hidden,
        busy: panel.getAttribute('aria-busy'),
        text: [...panel.querySelectorAll('p')].map(p => p.textContent).join(' '),
        loading: !!panel.querySelector('.loading .spinner'),
        headers: table ? [...table.querySelectorAll('thead th')].map(th => th.textContent) : null,
        rows: table ? [...table.querySelectorAll('tbody tr')].map(row => [...row.cells].map(cell => cell.textContent)) : null
    };
}

function assertFindings(page, expected) {
    const view = findingsView(page);
    assert.deepEqual(Object.fromEntries(Object.keys(expected).map(key => [key, view[key]])), expected);
}

// The jobs page listing `jobs`; the list and the findings answer `server.jobs` / `server.findings`.
async function openJobsPage(t, { jobs = [audit()], findings = findingsPage(FINDINGS_LIST), routes = {} } = {}) {
    const server = { jobs, findings };
    const page = await h.bootJobsApp(t, {
        hash: '#/jobs',
        routes: { [`GET ${LIST}`]: () => server.jobs, [`GET ${FINDINGS}`]: () => server.findings, ...routes }
    });
    return { page, server };
}

const toggleFindings = page => h.clickJobAction(page, 'metadata-audit', 'findings');

test('View findings lists the findings of the metadata audit', async (t) => {
    const answering = deferred();
    const { page } = await openJobsPage(t, { routes: { [`GET ${FINDINGS}`]: () => answering.promise } });
    const card = h.jobCard(page, 'metadata-audit');
    const panel = card.querySelector('[data-job-part="findings"]');
    assert.equal(card.querySelector('[data-job-action="findings"]').getAttribute('aria-controls'), panel.id);
    assertFindings(page, { button: 'View findings', expanded: 'false', hidden: true });
    assert.equal(findingsLoads(page).length, 0, 'findings are loaded only when asked for');

    toggleFindings(page);
    await page.settle();
    assertFindings(page, { button: 'Hide findings', expanded: 'true', hidden: false, busy: 'true', text: 'Loading findings...', loading: true });
    const [load] = findingsLoads(page);
    assert.deepEqual(load.query, { limit: '200' });
    assert.equal(load.init.credentials, 'include');
    assert.match(load.headers.authorization, /^Bearer /);

    answering.resolve(findingsPage(FINDINGS_LIST));
    await page.settle();
    assertFindings(page, {
        busy: 'false',
        text: '4 findings recorded.',
        loading: false,
        headers: ['Type', 'File', 'Expected series', 'Actual series', 'Details'],
        rows: FINDINGS_ROWS
    });
    assert.deepEqual(loadErrors(page), []);
});

test('Hide findings closes the panel and opening it again reloads the findings', async (t) => {
    const { page, server } = await openJobsPage(t);
    toggleFindings(page);
    await page.settle();
    assertFindings(page, { hidden: false, rows: FINDINGS_ROWS });

    toggleFindings(page);
    await page.settle();
    assertFindings(page, { button: 'View findings', expanded: 'false', hidden: true });
    assert.equal(findingsLoads(page).length, 1, 'closing loads nothing');

    server.findings = findingsPage([FINDINGS_LIST[3]]);
    toggleFindings(page);
    await page.settle();
    assert.equal(findingsLoads(page).length, 2);
    assertFindings(page, { button: 'Hide findings', expanded: 'true', hidden: false, text: '1 finding recorded.', rows: [FINDINGS_ROWS[3]] });
});

test('a long list says how many findings there are in all', async (t) => {
    const many = Array.from({ length: 200 }, (_, i) => finding(i + 1, `/watched/Archie/Archie ${String(i + 1).padStart(3, '0')}.cbz`, 'MissingChapter', {
        actualSeries: 'Archie', details: 'ComicInfo <Number> tag is empty.'
    }));
    const { page } = await openJobsPage(t, { findings: findingsPage(many, 201) });
    toggleFindings(page);
    await page.settle();
    const { text, rows } = findingsView(page);
    assert.equal(text, '201 findings recorded. Showing the first 200.');
    assert.equal(rows.length, 200);
    assert.deepEqual(rows[199], ['Missing chapter', '/watched/Archie/Archie 200.cbz', '', 'Archie', 'ComicInfo <Number> tag is empty.']);
});

test('incomplete findings are shown with blanks', async (t) => {
    // Without a total the rows are counted; missing fields stay empty.
    const { page } = await openJobsPage(t, {
        findings: { findings: [finding(1, '/watched/Odd/Odd 001.cbz', null), finding(2, '/watched/Odd/Odd 002.cbz', 'Unreadable')] }
    });
    toggleFindings(page);
    await page.settle();
    assertFindings(page, {
        text: '2 findings recorded.',
        rows: [['', '/watched/Odd/Odd 001.cbz', '', '', ''], ['Unreadable', '/watched/Odd/Odd 002.cbz', '', '', '']]
    });
});

test('no findings yet', async (t) => {
    for (const answer of [findingsPage([]), {}]) {
        const { page } = await openJobsPage(t, { findings: answer });
        toggleFindings(page);
        await page.settle();
        assertFindings(page, {
            busy: 'false',
            text: 'No findings recorded yet. Run the metadata audit to populate this list.',
            rows: null
        });
    }
});

test('findings that cannot be loaded say why in the panel', async (t) => {
    const failures = [
        { answer: h.unexpectedError(FINDINGS, 'req-500c'), message: h.UNEXPECTED_ERROR },
        { answer: 403, message: 'Only administrators can manage scheduled jobs.' },
        { answer: respond(502, 'Bad Gateway'), message: 'HTTP error! status: 502' },
        { answer: () => { throw new TypeError('Failed to fetch'); }, message: 'Failed to fetch' }
    ];
    for (const { answer, message } of failures) {
        const { page } = await openJobsPage(t, { routes: { [`GET ${FINDINGS}`]: answer } });
        toggleFindings(page);
        await page.settle();
        assertFindings(page, { hidden: false, busy: 'false', loading: false, text: `Findings could not be loaded: ${message}`, rows: null });
        assert.deepEqual(loadErrors(page), [message]);
        assert.deepEqual(h.messages(page), []);

        // Closing and opening the panel tries again.
        page.route(`GET ${FINDINGS}`, findingsPage(FINDINGS_LIST));
        toggleFindings(page);
        toggleFindings(page);
        await page.settle();
        assertFindings(page, { text: '4 findings recorded.', rows: FINDINGS_ROWS });
    }
});

test('findings refused for an expired session go to the login page', async (t) => {
    const { page } = await openJobsPage(t, { routes: { [`GET ${FINDINGS}`]: 401 } });
    toggleFindings(page);
    await page.settle();
    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(loadErrors(page), []);
    assertFindings(page, { busy: 'false', rows: null });
});

test('only the findings asked for last are shown', async (t) => {
    for (const stale of [findingsPage([FINDINGS_LIST[0]]), respond(500, { error: 'Database is locked' })]) {
        const answers = [deferred(), deferred()];
        const pending = [...answers];
        const { page } = await openJobsPage(t, { routes: { [`GET ${FINDINGS}`]: () => pending.shift().promise } });
        toggleFindings(page);
        toggleFindings(page);
        toggleFindings(page);
        await page.settle();
        assert.equal(findingsLoads(page).length, 2);

        answers[0].resolve(stale);
        await page.settle();
        assertFindings(page, { busy: 'true', text: 'Loading findings...', rows: null });
        assert.deepEqual(loadErrors(page), []);

        answers[1].resolve(findingsPage(FINDINGS_LIST));
        await page.settle();
        assertFindings(page, { busy: 'false', text: '4 findings recorded.', rows: FINDINGS_ROWS });
    }
});

test('the open findings stay open while the status refreshes', async (t) => {
    const { page, server } = await openJobsPage(t);
    toggleFindings(page);
    await page.settle();

    server.jobs = [audit({ lastStatus: 'Success', lastRunUtc: '2026-01-02T03:04:05', lastDurationMs: 9000 })];
    await h.tick(t, page, 30000);
    h.assertJobCard(page, 'metadata-audit', { status: 'Success' });
    assertFindings(page, { button: 'Hide findings', hidden: false, rows: FINDINGS_ROWS });
    assert.equal(findingsLoads(page).length, 1, 'a timer-driven run is not followed, so the findings are not reloaded');
});

test('an audit run from the page reloads the open findings when it finishes', async (t) => {
    const { page, server } = await openJobsPage(t, {
        findings: findingsPage([FINDINGS_LIST[0]]),
        routes: { [`POST ${RUN}`]: respond(202, { jobKey: 'metadata-audit', queued: true }) }
    });
    toggleFindings(page);
    await page.settle();
    assertFindings(page, { rows: [FINDINGS_ROWS[0]] });

    server.jobs = [audit({ lastStatus: 'Running' })];
    h.clickJobAction(page, 'metadata-audit', 'run');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Metadata Audit started.' }]);
    assert.equal(findingsLoads(page).length, 1);

    server.jobs = [audit({
        lastStatus: 'Success', lastRunUtc: '2026-01-02T03:04:05', lastDurationMs: 9000,
        lastMessage: 'Scanned 120: 1 series mismatch, 1 missing chapter, 1 unreadable.'
    })];
    server.findings = findingsPage(FINDINGS_LIST);
    await h.tick(t, page, 2000);
    assert.deepEqual(h.takeMessages(page), [{ type: 'success', text: 'Metadata Audit finished: Scanned 120: 1 series mismatch, 1 missing chapter, 1 unreadable.' }]);
    assert.equal(findingsLoads(page).length, 2);
    assertFindings(page, { hidden: false, text: '4 findings recorded.', rows: FINDINGS_ROWS });
});

test('an audit run from the page does not load closed findings', async (t) => {
    const { page, server } = await openJobsPage(t, { routes: { [`POST ${RUN}`]: respond(202, { jobKey: 'metadata-audit', queued: true }) } });
    server.jobs = [audit({ lastStatus: 'Failed', lastRunUtc: '2026-01-02T03:04:05', lastDurationMs: 12, lastMessage: 'Database is locked' })];
    h.clickJobAction(page, 'metadata-audit', 'run');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [
        { type: 'info', text: 'Metadata Audit started.' },
        { type: 'error', text: 'Metadata Audit failed: Database is locked' }
    ]);
    assert.equal(findingsLoads(page).length, 0);
    assertFindings(page, { hidden: true });
});

test('only the metadata audit has findings to show', async (t) => {
    const { page } = await openJobsPage(t, { jobs: [h.scheduledJob(), audit()] });
    assert.equal(h.jobCard(page, 'library-scan').querySelector('[data-job-part="findings"]'), null);
    await page.run("toggleMetadataAuditFindings('library-scan')");
    await page.run("toggleMetadataAuditFindings('no-such-job')");
    await page.settle();
    assert.equal(findingsLoads(page).length, 0);
    assertFindings(page, { hidden: true });
    assert.deepEqual(page.errors, []);
});
