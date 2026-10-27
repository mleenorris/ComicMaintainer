'use strict';

// Shared setup for the batch-job and scheduled-jobs page tests
// (tests/js/jobs-*.test.js, tests/js/scheduled-jobs-*.test.js): boots the
// signed-in SPA with quiet defaults and mocked timers, answers confirm()
// dialogs from a queue, delivers server-sent job events and reads the
// progress modal back as plain data.

const assert = require('node:assert/strict');
const { bootApp } = require('./app');
const { respond } = require('./dom');

// Job ids as JobsController formats Guids (the client only resumes UUIDs).
const JOB_ID = '6f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b';
const OTHER_JOB_ID = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d';

// Base64url without padding, as encodeFilePathForUrl() (js/app/api.js) puts file paths into URLs.
const b64 = filePath => Buffer.from(filePath, 'utf8').toString('base64url');

// GET /api/jobs/{id} as JobsController.GetJob returns it. `results` lists the
// job's files; tests list only finished ones (see the final report on
// pollJobStatusOnce counting unfinished files as failures).
function jobStatus(overrides = {}) {
    return {
        job_id: JOB_ID,
        status: 'running',
        total_items: 4,
        processed_items: 0,
        failed_items: 0,
        current_file: null,
        start_time: '2026-01-02T03:04:05Z',
        end_time: null,
        results: [],
        ...overrides
    };
}

// A job_updated progress payload as EventBroadcasterService.BroadcastJobUpdateAsync sends it.
function progress(processed, total, success = processed, errors = 0) {
    return { processed, total, success, errors, percentage: total > 0 ? Math.floor((processed * 100) / total) : 0 };
}

// An issue as GET /api/files/series/{id}/issues lists it.
function seriesIssue(filePath, overrides = {}) {
    const fileName = filePath.split('/').pop();
    return {
        file_path: filePath,
        file_name: fileName,
        title: fileName.replace(/\.cb[zr]$/, ''),
        issue: (fileName.match(/(\d+)/) || [null, '1'])[1],
        size: 1000,
        modified: 0,
        read: false,
        ...overrides
    };
}

function seriesIssues(issues, { id = 's1', title = 'Batman' } = {}) {
    return { id, title, issue_count: issues.length, issues, page: 1, per_page: -1, total_pages: 1 };
}

// The two issues bootSeriesDetail() shows by default.
const ISSUE_A = 'Batman/Batman 001.cbz';
const ISSUE_B = 'Batman/Batman 002.cbz';

// A scheduled job as GET /api/scheduled-jobs lists it (ScheduledJobDto).
function scheduledJob(overrides = {}) {
    return {
        jobKey: 'library-scan',
        displayName: 'Library Scan',
        description: 'Walks the watched directory, adds new files and reconciles deletions.',
        enabled: true,
        intervalMinutes: 60,
        lastRunUtc: null,
        nextRunUtc: null,
        lastDurationMs: null,
        lastStatus: 'NeverRun',
        lastMessage: null,
        optionsJson: null,
        ...overrides
    };
}

// NotFound() as an [ApiController] sends it: problem details without an error message.
const NOT_FOUND_PROBLEM = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
    title: 'Not Found',
    status: 404,
    traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00'
};

// GlobalExceptionHandler's answer to an unhandled exception (production).
const UNEXPECTED_ERROR = 'The request could not be completed. Quote the reference below when reporting this.';
function unexpectedError(path, reference) {
    return respond(500, {
        type: 'https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1',
        title: 'An unexpected error occurred.',
        status: 500,
        detail: UNEXPECTED_ERROR,
        instance: path,
        correlationId: reference,
        error: UNEXPECTED_ERROR
    }, { 'X-Correlation-Id': reference });
}

// The request loadActiveLibraryView(1, true) sends to reload the series library.
const LIBRARY_RELOAD = /^\/api\/files\/series\?offset=0&limit=60&refresh=true$/;

// The job the progress tests track: Process Unmarked (one confirm(), no
// force prompt) started as JOB_ID over 4 files.
const TRACKED_TITLE = 'Processing Unmarked Files...';

// Routes for that job. GET /api/jobs/{JOB_ID} is answered by `poll.answer`,
// which a test may replace (a status object, a status code or a respond()).
function trackedJob(initialStatus = jobStatus()) {
    const poll = { answer: () => initialStatus };
    return {
        poll,
        routes: {
            'POST /api/jobs/process-unmarked': { job_id: JOB_ID, total_items: 4 },
            [`GET /api/jobs/${JOB_ID}`]: call => poll.answer(call)
        }
    };
}

// Requests every library boot makes, answered so that the boot stays quiet.
function quietRoutes() {
    return {
        'GET /api/active-job': { active: false },
        'GET /api/files/series': { series: [], total_series: 0, unmarked_count: 0 }
    };
}

function enableMockTimers(t) {
    try {
        t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    } catch (error) {
        // Already enabled by an earlier boot in the same test.
        if (error.code !== 'ERR_INVALID_STATE') throw error;
    }
}

/**
 * bootApp() with node:test mock timers enabled first (the page arms timers
 * while booting) and window.confirm answered from `page.confirmAnswers`
 * (true once the queue is empty). Other options go to bootApp().
 */
async function bootJobsApp(t, { hash = '#/library', routes = {}, timers = true, ...options } = {}) {
    if (timers) enableMockTimers(t);
    const page = await bootApp({ hash, routes: { ...quietRoutes(), ...routes }, ...options });
    page.confirmAnswers = [];
    page.window.confirm = message => {
        page.dialogs.push({ kind: 'confirm', message });
        return page.confirmAnswers.length ? page.confirmAnswers.shift() : true;
    };
    return page;
}

// bootJobsApp() on the detail page of series "s1" (#/series/s1) listing `issues`.
async function bootSeriesDetail(t, { issues = [seriesIssue(ISSUE_A), seriesIssue(ISSUE_B)], routes = {}, ...options } = {}) {
    return bootJobsApp(t, {
        hash: '#/series/s1',
        routes: { 'GET /api/files/series/s1/issues': seriesIssues(issues), ...routes },
        ...options
    });
}

// Queue the answers of the next confirm() dialogs.
function answerConfirms(page, ...answers) {
    page.confirmAnswers.push(...answers);
}

// The confirm() messages shown since the last call, which forgets them.
function takeConfirms(page) {
    const shown = page.dialogs.filter(dialog => dialog.kind === 'confirm').map(dialog => dialog.message);
    page.dialogs.length = 0;
    return shown;
}

// The toasts showMessage() is currently showing.
function messages(page) {
    return page.$$('#messageContainer .message').map(element => ({
        type: element.className.replace(/^message\s*/, ''),
        text: element.textContent
    }));
}

// messages(), then removes them so the next step starts from none.
function takeMessages(page) {
    const shown = messages(page);
    page.$('#messageContainer').replaceChildren();
    return shown;
}

// Advance the mocked timers, then let the promise chains and fetches they start finish.
async function tick(t, page, ms) {
    t.mock.timers.tick(ms);
    await page.settle();
}

// Deliver a server-sent event the way EventBroadcasterService sends it.
function serverEvent(page, type, data) {
    page.eventSources.at(-1).emit('message', { type, data });
}

function jobUpdate(page, status, progressData, jobId = JOB_ID) {
    serverEvent(page, 'job_updated', { job_id: jobId, status, progress: progressData });
}

function fileProcessed(page, filename, success, error = null) {
    serverEvent(page, 'file_processed', { filename, success, error });
}

// The progress modal and its minimized indicator, as plain data.
function progressView(page) {
    const byId = id => page.document.getElementById(id);
    return {
        open: byId('progressModal').classList.contains('active'),
        title: byId('progressTitle').textContent,
        text: byId('progressText').textContent,
        percent: byId('progressPercent').textContent,
        bar: byId('progressBarFill').style.width,
        processed: byId('progressProcessedCount').textContent,
        succeeded: byId('progressSuccessCount').textContent,
        failed: byId('progressErrorCount').textContent,
        currentFile: byId('progressCurrentFile').textContent,
        resultsCount: byId('progressResultsCount').textContent,
        results: page.$$('#progressDetails .progress-result-item').map(row => ({
            success: row.classList.contains('success'),
            file: row.querySelector('strong').textContent,
            detail: row.querySelector('span').textContent
        })),
        emptyResults: Boolean(page.$('#progressDetails .progress-results-empty')),
        cancelVisible: byId('progressCancelBtn').style.display !== 'none',
        closeVisible: byId('progressCloseBtn').style.display !== 'none',
        indicatorVisible: byId('progressIndicator').style.display !== 'none',
        indicatorText: byId('progressIndicatorText').textContent
    };
}

// Assert the given fields of progressView().
function assertProgress(page, expected) {
    const view = progressView(page);
    assert.deepEqual(Object.fromEntries(Object.keys(expected).map(key => [key, view[key]])), expected);
}

// The progress modal right after showProgressModal(title), before the job reports anything.
function startingProgress(title) {
    return {
        open: true,
        title,
        text: '0 / 0 files',
        percent: '0%',
        bar: '0%',
        processed: '0',
        succeeded: '0',
        failed: '0',
        currentFile: 'Waiting for the job to start...',
        resultsCount: '0 results',
        results: [],
        emptyResults: true,
        cancelVisible: true,
        closeVisible: false,
        indicatorVisible: false
    };
}

// An item of one of the library toolbar's action menus, by its label.
function menuItem(page, menuId, label) {
    const item = page.$$(`#${menuId} .action-dropdown-item`).find(element => element.textContent.trim() === label);
    if (!item) throw new Error(`No "${label}" item in #${menuId}`);
    return item;
}

// Open a toolbar action menu and click one of its items, as a user would.
function chooseMenuItem(page, menuId, label) {
    const menu = page.$(`#${menuId}`);
    const item = menuItem(page, menuId, label);
    page.click(menu.parentElement.querySelector('.action-dropdown-toggle'));
    assert.ok(menu.classList.contains('show'), `#${menuId} opens`);
    page.click(item);
    return item;
}

function issueCard(page, filePath) {
    const card = page.$$('.series-issue-card[data-file-path]').find(element => element.dataset.filePath === filePath);
    if (!card) throw new Error(`No issue card for ${filePath}`);
    return card;
}

// Open a series issue card's actions menu (toggleDropdown() moves it to
// <body> while it is open) and click one of its items, as a user would.
function chooseIssueAction(page, filePath, label) {
    page.click(issueCard(page, filePath).querySelector('.series-issue-actions-toggle'));
    const menu = page.document.getElementById(page.run(`getDropdownId(${JSON.stringify(filePath)})`));
    assert.ok(menu && menu.classList.contains('show'), `the actions menu of ${filePath} opens`);
    const item = [...menu.querySelectorAll('.dropdown-item')].find(element => element.textContent.trim() === label);
    if (!item) throw new Error(`No "${label}" action on ${filePath}`);
    page.click(item);
    return item;
}

// Start the trackedJob() from the toolbar and wait for its first status poll;
// the confirm() and the "Started ..." toast are forgotten.
async function startTrackedJob(page) {
    chooseMenuItem(page, 'processDropdown', 'Process Unmarked');
    await page.settle();
    takeConfirms(page);
    takeMessages(page);
}

// Tick the checkbox of a series issue card, as a user selecting it would.
function selectIssue(page, filePath, checked = true) {
    const checkbox = issueCard(page, filePath).querySelector('.series-issue-select input[type="checkbox"]');
    page.input(checkbox, checked);
}

// bootJobsApp() on the jobs page (#/jobs) listing `jobs`: GET /api/scheduled-jobs
// answers `server.jobs`, which a test replaces to change what the next load reads.
async function openScheduledJobsPage(t, jobs, routes = {}) {
    const server = { jobs };
    const page = await bootJobsApp(t, { hash: '#/jobs', routes: { 'GET /api/scheduled-jobs': () => server.jobs, ...routes } });
    return { page, server };
}

// A scheduled job's card on the jobs page (#/jobs).
function jobCard(page, jobKey) {
    const card = page.$$('#scheduledJobsList [data-job-key]').find(element => element.dataset.jobKey === jobKey);
    if (!card) throw new Error(`No card for scheduled job ${jobKey}`);
    return card;
}

// The job keys of the cards on the jobs page, in order.
function jobCardKeys(page) {
    return page.$$('#scheduledJobsList [data-job-key]').map(card => card.dataset.jobKey);
}

// A job card as plain data: what the user reads and the state of its controls.
function jobCardView(page, jobKey) {
    const card = jobCard(page, jobKey);
    const part = name => card.querySelector(`[data-job-part="${name}"]`);
    const action = name => card.querySelector(`[data-job-action="${name}"]`);
    const badge = part('status').querySelector('.scheduled-job-status');
    const description = card.querySelector('.settings-card-desc');
    const findings = action('findings');
    return {
        title: card.querySelector('.settings-card-title').textContent,
        icon: card.querySelector('.settings-card-icon use').getAttribute('href'),
        description: description ? description.textContent : null,
        status: badge.textContent.replace(/^Last run status: /, ''),
        statusClass: [...badge.classList].find(name => name.startsWith('scheduled-job-status--')),
        runs: Object.fromEntries([...part('runs').querySelectorAll('.scheduled-job-run')]
            .map(row => [row.querySelector('dt').textContent, row.querySelector('dd').textContent])),
        enabled: card.querySelector('[data-job-field="enabled"]').checked,
        interval: card.querySelector('[data-job-field="intervalMinutes"]').value,
        hint: part('interval-hint').textContent,
        options: Object.fromEntries([...card.querySelectorAll('[data-job-option]')]
            .map(input => [input.dataset.jobOption, input.checked])),
        run: action('run').textContent.trim(),
        runDisabled: action('run').disabled,
        saveDisabled: action('save').disabled,
        findings: findings ? findings.textContent.trim() : null
    };
}

// Assert the given fields of jobCardView().
function assertJobCard(page, jobKey, expected) {
    const view = jobCardView(page, jobKey);
    assert.deepEqual(Object.fromEntries(Object.keys(expected).map(key => [key, view[key]])), expected);
}

// Click one of a job card's buttons (save, run, findings), as a user would.
function clickJobAction(page, jobKey, action) {
    const button = jobCard(page, jobKey).querySelector(`[data-job-action="${action}"]`);
    if (!button) throw new Error(`No ${action} button on the ${jobKey} card`);
    page.click(button);
    return button;
}

// Change one of a job card's fields (enabled, intervalMinutes) or options, as a user would.
function editJobField(page, jobKey, field, value) {
    const card = jobCard(page, jobKey);
    const input = card.querySelector(`[data-job-field="${field}"]`) || card.querySelector(`[data-job-option="${field}"]`);
    if (!input) throw new Error(`No ${field} field on the ${jobKey} card`);
    page.input(input, value);
    return input;
}

// Freeze the page's Date.now() and move it only with the mocked timers: the
// scheduled jobs page times out runs that never start by Date.now(), which
// mocked timers do not move. advance(ms) moves both by `ms`.
function pageClock(t, page) {
    const { Date } = page.window;
    const start = Date.now();
    let offset = 0;
    Date.now = () => start + offset;
    return {
        async advance(ms) {
            offset += ms;
            await tick(t, page, ms);
        }
    };
}

// How many times the page was sent to the login page (handleAuthError() on a 401).
function loginRedirects(page) {
    return page.navigations.filter(navigation => navigation.url === 'http://localhost/login.html').length;
}

// The requests sent so far with this method whose path equals `path` (or whose URL matches a RegExp).
function requests(page, method, path) {
    return page.fetchCalls.filter(call => call.method === method
        && (path instanceof RegExp ? path.test(call.url) : call.path === path));
}

module.exports = {
    JOB_ID,
    OTHER_JOB_ID,
    b64,
    jobStatus,
    progress,
    seriesIssue,
    seriesIssues,
    ISSUE_A,
    ISSUE_B,
    scheduledJob,
    NOT_FOUND_PROBLEM,
    UNEXPECTED_ERROR,
    unexpectedError,
    LIBRARY_RELOAD,
    TRACKED_TITLE,
    trackedJob,
    quietRoutes,
    bootJobsApp,
    bootSeriesDetail,
    answerConfirms,
    takeConfirms,
    messages,
    takeMessages,
    tick,
    serverEvent,
    jobUpdate,
    fileProcessed,
    progressView,
    assertProgress,
    startingProgress,
    menuItem,
    chooseMenuItem,
    chooseIssueAction,
    startTrackedJob,
    selectIssue,
    openScheduledJobsPage,
    jobCard,
    jobCardKeys,
    jobCardView,
    assertJobCard,
    clickJobAction,
    editJobField,
    pageClock,
    loginRedirects,
    requests
};
