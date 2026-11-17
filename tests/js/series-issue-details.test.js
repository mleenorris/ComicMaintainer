'use strict';

// js/app/series.js: Fetch Issue Details and story arcs. "Fetch Issue
// Details" starts a job that fills the series' issues from ComicVine
// (POST /api/metadata/issue-details/series/{id}). The story arcs the issues
// are part of (GET /api/metadata/story-arcs/series/{id}) are shown under the
// synopsis; each opens its reading order (GET /api/metadata/story-arcs/{id}).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootSeriesPage,
    seriesSummary,
    seriesIssue,
    messageTexts,
    isModalOpen,
    pressEscape,
    buttonByText,
    clickSeriesAction,
    callsTo
} = require('./support/email-helpers');

const SERIES = seriesSummary({ id: 'batman', title: 'Batman', issue_count: 2 });
const ISSUES = [
    seriesIssue({ file_path: 'Batman/Batman 001.cbz', file_name: 'Batman 001.cbz', title: 'Part One', issue: '1' }),
    seriesIssue({ file_path: 'Batman/Batman 002.cbz', file_name: 'Batman 002.cbz', title: 'Part Two', issue: '2' })
];
const ARCS = 'GET /api/metadata/story-arcs/series/batman';
const ARC = 'GET /api/metadata/story-arcs/7';
const FETCH = 'POST /api/metadata/issue-details/series/batman';

const SERIES_ARCS = {
    arcs: [
        { id: 7, name: 'I Am Gotham', file_paths: ['Batman/Batman 001.cbz', 'Batman/Batman 002.cbz'] },
        { id: 8, name: 'Night of the Monster Men', file_paths: ['Batman/Batman 002.cbz'] },
        { id: 9, name: 'Odd <Arc>' }
    ]
};

const ARC_7 = {
    id: 7,
    name: 'I Am Gotham',
    summary: 'Gotham and Gotham Girl.',
    site_url: 'https://comicvine.example/arc-7',
    issues: [
        { id: 101, name: 'Part One', issue_number: '1', volume: 'Batman', file_path: 'Batman/Batman 001.cbz' },
        { id: 103, name: null, issue_number: '3', volume: 'Batman', file_path: null },
        { id: 104, name: null, issue_number: null, volume: null, file_path: null }
    ]
};

function boot(options = {}) {
    return bootSeriesPage({ series: SERIES, issues: ISSUES, ...options });
}

function chips(page) {
    return page.$$('.series-story-arc-chip').map(chip => chip.textContent);
}

function arcItems(page) {
    return page.$$('#storyArcIssues .story-arc-issue').map(item => item.textContent.replace(/\s+/g, ' ').trim());
}

test('the story arcs of the series show under its synopsis and open their reading order', async t => {
    const page = await boot({ routes: { [ARCS]: SERIES_ARCS, [ARC]: ARC_7 } });
    t.after(() => page.close());
    await page.settle();

    assert.deepEqual(chips(page), ['I Am Gotham · 2 issues', 'Night of the Monster Men · 1 issue', 'Odd <Arc> · 0 issues']);

    page.click(page.$$('.series-story-arc-chip')[0]);
    await page.settle();

    assert.ok(isModalOpen(page, 'storyArcModal'));
    assert.equal(page.$('#storyArcModalTitle').textContent, 'I Am Gotham');
    assert.equal(page.$('#storyArcSummary').textContent, 'Gotham and Gotham Girl. You have 1 of its 3 issues.');
    assert.equal(page.$('#storyArcLink').getAttribute('href'), 'https://comicvine.example/arc-7');
    assert.equal(page.$('#storyArcLink').style.display, '');
    assert.deepEqual(arcItems(page), [
        'Batman #1 · Part One Read',
        'Batman #3 Not in your library',
        'Unknown volume Not in your library'
    ]);

    page.click(buttonByText(page, '#storyArcModal .modal-footer', 'Close'));
    assert.ok(!isModalOpen(page, 'storyArcModal'));
});

test('a story arc without a summary, link or issues says so', async t => {
    const page = await boot({
        routes: { [ARCS]: SERIES_ARCS, 'GET /api/metadata/story-arcs/8': { id: 8, name: 'Night of the Monster Men', summary: null, site_url: 'javascript:alert(1)', issues: null } }
    });
    t.after(() => page.close());

    await page.run('openStoryArcModal(8)');

    assert.equal(page.$('#storyArcSummary').textContent, 'You have 0 of its 0 issues.');
    assert.equal(page.$('#storyArcLink').style.display, 'none');
    assert.equal(page.$('#storyArcIssues').textContent.trim(), 'ComicVine lists no issues for this story arc.');
    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'storyArcModal'));
});

test('a story arc ComicVine does not have, or cannot give, is reported in the dialog', async t => {
    const page = await boot({
        routes: {
            [ARCS]: SERIES_ARCS,
            [ARC]: respond(404, { error: 'Story arc not found' }),
            'GET /api/metadata/story-arcs/8': respond(503, { error: 'ComicVine is limiting requests' }),
            'GET /api/metadata/story-arcs/9': 401
        }
    });
    t.after(() => page.close());

    await page.run('openStoryArcModal(7)');
    assert.equal(page.$('#storyArcIssues').textContent, 'ComicVine has no such story arc.');
    await page.run('openStoryArcModal(8)');
    assert.equal(page.$('#storyArcIssues').textContent, 'The story arc could not be loaded: ComicVine is limiting requests');
    await page.run('openStoryArcModal(9)');
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
});

test('a late answer for a story arc no longer shown is dropped', async t => {
    const first = deferred();
    const failing = deferred();
    const page = await boot({
        routes: {
            [ARCS]: SERIES_ARCS,
            [ARC]: () => first.promise,
            'GET /api/metadata/story-arcs/8': () => failing.promise,
            'GET /api/metadata/story-arcs/9': { id: 9, name: 'Odd <Arc>', issues: [] }
        }
    });
    t.after(() => page.close());

    const openFirst = page.run('openStoryArcModal(7)');
    const openSecond = page.run('openStoryArcModal(8)');
    await page.run('openStoryArcModal(9)');
    first.resolve(ARC_7);
    failing.resolve(respond(500, { error: 'boom' }));
    await openFirst;
    await openSecond;

    assert.equal(page.$('#storyArcModalTitle').textContent, 'Odd <Arc>');
    await page.run('closeStoryArcModal()');
});

test('a series whose story arcs cannot be read, or that has none, shows no story arcs', async t => {
    const page = await boot({ routes: { [ARCS]: respond(500, { error: 'boom' }) } });
    t.after(() => page.close());
    await page.settle();
    assert.deepEqual(chips(page), []);

    const none = await boot({ routes: { [ARCS]: { arcs: null } } });
    t.after(() => none.close());
    await none.settle();
    assert.deepEqual(chips(none), []);

    const expired = await boot({ routes: { [ARCS]: 401 } });
    t.after(() => expired.close());
    await expired.settle();
    assert.deepEqual(chips(expired), []);
});

test('the story arcs are read again when the issues reload, and a stale answer is dropped', async t => {
    const answers = [deferred(), deferred()];
    let call = 0;
    const page = await boot({ routes: { [ARCS]: () => answers[call++].promise } });
    t.after(() => page.close());
    await page.settle();
    assert.equal(callsTo(page, 'GET', '/api/metadata/story-arcs/series/batman').length, 1);

    // The issues reload while the arcs are still being read: that load asks again.
    await page.run('loadSeriesIssues("batman", true, { immediate: true })');
    await page.settle();
    answers[0].resolve({ arcs: [{ id: 1, name: 'Stale', file_paths: [] }] });
    await page.settle();
    assert.deepEqual(chips(page), []);

    answers[1].resolve(SERIES_ARCS);
    await page.settle();
    assert.equal(callsTo(page, 'GET', '/api/metadata/story-arcs/series/batman').length, 2);
    assert.equal(chips(page).length, 3);
});

test('Fetch Issue Details starts the job that fills the series from ComicVine', async t => {
    const page = await boot({
        routes: {
            [ARCS]: { arcs: [] },
            [FETCH]: { job_id: 'job-6', total_items: 2 },
            'GET /api/jobs/job-6': { job_id: 'job-6', status: 'running', total_items: 2, processed_items: 0, results: [] }
        }
    });
    t.after(() => page.close());

    clickSeriesAction(page, 'Fetch Issue Details');
    await page.settle();

    assert.equal(callsTo(page, 'POST', '/api/metadata/issue-details/series/batman').length, 1);
    assert.deepEqual(messageTexts(page, 'info'), ['Fetching ComicVine issue details for 2 issues of Batman']);
    assert.ok(isModalOpen(page, 'progressModal'));
    assert.equal(page.$('#progressTitle').textContent, 'Fetching Issue Details...');
    assert.equal(callsTo(page, 'GET', '/api/jobs/job-6').length, 1);

    // While that job runs no second one starts.
    await page.run('fetchSeriesIssueDetails("batman", "Batman")');
    assert.equal(callsTo(page, 'POST', '/api/metadata/issue-details/series/batman').length, 1);
    assert.match(messageTexts(page, 'warning')[0], /Another job is already in progress/);
});

test('Fetch Issue Details reports a job the server did not start, and an expired session goes to login', async t => {
    const page = await boot({ routes: { [ARCS]: { arcs: [] }, [FETCH]: respond(404, { error: 'Series not found' }) } });
    t.after(() => page.close());

    await page.run('fetchSeriesIssueDetails("batman", "Batman")');
    assert.deepEqual(messageTexts(page, 'error'), ['Failed to fetch issue details: Series not found']);
    assert.ok(!isModalOpen(page, 'progressModal'));

    page.route(FETCH, { job_id: 'job-1', total_items: 1 });
    page.route('GET /api/jobs/job-1', { job_id: 'job-1', status: 'running', total_items: 1, processed_items: 0, results: [] });
    await page.run('fetchSeriesIssueDetails("batman", "Batman")');
    assert.deepEqual(messageTexts(page, 'info'), ['Fetching ComicVine issue details for 1 issue of Batman']);

    const expired = await boot({ routes: { [ARCS]: { arcs: [] }, [FETCH]: 401 } });
    t.after(() => expired.close());
    await expired.run('fetchSeriesIssueDetails("batman", "Batman")');
    assert.ok(expired.navigations.some(nav => nav.url.includes('/login.html')));
    assert.deepEqual(messageTexts(expired, 'error'), []);
});
