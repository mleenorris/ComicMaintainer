'use strict';

// js/app/series.js: Replace Cover. An issue's menu offers "Replace Cover…",
// which lists the covers ComicVine has for the issue's volume
// (GET /api/covers/candidates) with the issue's own chosen, and makes the
// chosen one page 1 of the CBZ (POST /api/covers/replace). The page then
// shows the new cover, past the browser's and its own cached copies.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred, addReadOnlyGate } = require('./support/app');
const {
    bootSeriesPage,
    seriesSummary,
    seriesIssue,
    messageTexts,
    isModalOpen,
    pressEscape,
    buttonByText,
    clickIssueAction,
    callsTo
} = require('./support/email-helpers');

const FILE = 'Batman/Batman 002.cbz';
const OTHER = 'Batman/Batman 001.cbz';
const SERIES = seriesSummary({ id: 'batman', title: 'Batman', issue_count: 2, cover_file_path: FILE });
const ISSUES = [
    seriesIssue({ file_path: OTHER, file_name: 'Batman 001.cbz', title: 'One', issue: '1' }),
    seriesIssue({ file_path: FILE, file_name: 'Batman 002.cbz', title: 'Two', issue: '2' })
];
const CANDIDATES = `GET /api/covers/candidates?filePath=${encodeURIComponent(FILE)}`;
const REPLACE = 'POST /api/covers/replace';
const COVER = `/api/comicreader/cover?filePath=${encodeURIComponent(FILE)}`;

// GET /api/covers/candidates (CoversController.Candidates).
function candidates(overrides = {}) {
    return {
        volume_id: 91273,
        volume_name: 'Batman',
        matched_issue_id: 102,
        reason: null,
        covers: [
            { issue_id: 101, issue_number: '1', name: 'One', cover_url: 'https://cv.example/101.jpg', thumb_url: 'https://cv.example/101-m.jpg', matched: false },
            { issue_id: 102, issue_number: '2', name: null, cover_url: 'https://cv.example/102.jpg', thumb_url: null, matched: true }
        ],
        ...overrides
    };
}

function boot(options = {}) {
    return bootSeriesPage({ series: SERIES, issues: ISSUES, ...options });
}

// ComicVine refuses to show its images on other sites, so each cover is
// loaded through the server like any protected image.
function options(page) {
    return page.$$('#replaceCoverList .replace-cover-option').map(option => ({
        label: option.textContent.trim(),
        image: option.querySelector('img').dataset.protectedImage,
        selected: option.classList.contains('selected') && option.getAttribute('aria-pressed') === 'true'
    }));
}

const IMAGE_ROUTES = {
    'GET /api/covers/candidates/101/image': respond(200, 'image-bytes', { 'Content-Type': 'image/jpeg' }),
    'GET /api/covers/candidates/102/image': respond(200, 'image-bytes', { 'Content-Type': 'image/jpeg' })
};

async function openReplaceCover(page) {
    clickIssueAction(page, FILE, 'Replace Cover…');
    await page.settle();
}

test('Replace Cover lists the volume\'s covers from ComicVine with the issue\'s own chosen', async t => {
    const page = await boot({ routes: { [CANDIDATES]: candidates(), ...IMAGE_ROUTES } });
    t.after(() => page.close());

    await openReplaceCover(page);

    assert.ok(isModalOpen(page, 'replaceCoverModal'));
    assert.equal(page.$('#replaceCoverCurrentImage').dataset.protectedImage, FILE);
    assert.equal(page.$('#replaceCoverNote').textContent, 'Covers of Batman on ComicVine.');
    assert.deepEqual(options(page), [
        { label: '#1 One', image: '/api/covers/candidates/101/image?size=thumb', selected: false },
        { label: '#2 (this issue)', image: '/api/covers/candidates/102/image?size=thumb', selected: true }
    ]);
    assert.equal(page.$('#replaceCoverApplyBtn').disabled, false);
    // The covers come from the server, never straight from ComicVine, once they are in view.
    const observer = page.intersectionObservers.find(o => o.options && o.options.rootMargin === '400px 0px');
    observer.trigger(true, page.$$('#replaceCoverList img'));
    await page.settle();
    assert.ok(page.fetchCalls.some(call => call.url === '/api/covers/candidates/101/image?size=thumb'));
    assert.ok(page.$$('#replaceCoverList img').every(image => String(image.getAttribute('src')).startsWith('blob:')));
    assert.ok(page.$$('#replaceCoverList img').every(image => !String(image.getAttribute('src') || '').includes('cv.example')));
});

test('Replace Cover says why the issue has no match, and that there is nothing to choose', async t => {
    const page = await boot({
        routes: {
            [CANDIDATES]: candidates({ matched_issue_id: null, reason: 'ComicVine\'s Batman has no issue #2; pick its cover from the others' })
        }
    });
    t.after(() => page.close());

    await openReplaceCover(page);
    assert.equal(page.$('#replaceCoverNote').textContent, 'Covers of Batman on ComicVine. ComicVine\'s Batman has no issue #2; pick its cover from the others');
    assert.ok(options(page).every(option => !option.selected));
    assert.equal(page.$('#replaceCoverApplyBtn').disabled, true);

    page.route(CANDIDATES, { volume_name: null, reason: 'Annuals, Specials and Giant-Size issues are not matched to ComicVine yet', covers: null });
    await page.run(`openReplaceCoverModal(${JSON.stringify(FILE)})`);
    assert.equal(page.$('#replaceCoverNote').textContent, 'Annuals, Specials and Giant-Size issues are not matched to ComicVine yet');
    assert.equal(page.$('#replaceCoverList').textContent, 'ComicVine has no covers to choose from.');
});

test('Replace Cover writes the chosen cover and shows it in place of the old one', async t => {
    const page = await boot({ routes: { [CANDIDATES]: candidates(), [REPLACE]: { replaced: true }, [`GET ${COVER}`]: respond(200, 'cover') } });
    t.after(() => page.close());
    await page.settle();
    // The page holds the old cover as a blob, and an image shows it.
    const coverImage = () => page.$$('img[data-protected-image]').find(image => image.dataset.protectedImage === COVER);
    page.run(`protectedImageUrls.set(${JSON.stringify(COVER)}, 'blob:old-cover')`);
    coverImage().src = 'blob:old-cover';
    coverImage().dataset.protectedImageLoaded = '1';

    await openReplaceCover(page);
    page.click(page.$$('#replaceCoverList .replace-cover-option')[0]);
    assert.deepEqual(options(page).map(option => option.selected), [true, false]);
    page.click(page.$('#replaceCoverApplyBtn'));
    await page.settle();

    const [call] = callsTo(page, 'POST', '/api/covers/replace');
    assert.deepEqual(JSON.parse(JSON.stringify(call.body)), { FilePath: FILE, ComicVineIssueId: 101 });
    assert.ok(!isModalOpen(page, 'replaceCoverModal'));
    assert.deepEqual(messageTexts(page, 'success'), ['Cover replaced']);
    // The cover is fetched again past the browser's copy, and drawn again.
    const reloads = page.fetchCalls.filter(c => c.url === COVER && c.init.cache === 'reload');
    assert.equal(reloads.length, 1);
    assert.notEqual(page.run(`protectedImageUrls.get(${JSON.stringify(COVER)})`), 'blob:old-cover');
    assert.notEqual(coverImage().getAttribute('src'), 'blob:old-cover');
});

test('Replace Cover with the cover the issue already has says so and redraws nothing', async t => {
    const page = await boot({ routes: { [CANDIDATES]: candidates(), [REPLACE]: { replaced: false } } });
    t.after(() => page.close());

    await openReplaceCover(page);
    await page.run('applyReplaceCover()');

    assert.deepEqual(messageTexts(page, 'info'), ['The issue already has that cover']);
    assert.equal(page.fetchCalls.filter(c => c.url === COVER && c.init.cache === 'reload').length, 0);
});

test('Replace Cover reports a refusal and keeps the dialog open to try another', async t => {
    const page = await boot({
        routes: { [CANDIDATES]: candidates(), [REPLACE]: respond(400, { error: 'Only a CBZ\'s cover can be replaced; convert this file to CBZ first' }) }
    });
    t.after(() => page.close());

    await openReplaceCover(page);
    await page.run('applyReplaceCover()');

    assert.deepEqual(messageTexts(page, 'error'), ['Failed to replace the cover: Only a CBZ\'s cover can be replaced; convert this file to CBZ first']);
    assert.ok(isModalOpen(page, 'replaceCoverModal'));
    assert.equal(page.$('#replaceCoverApplyBtn').disabled, false);

    page.route(REPLACE, 401);
    await page.run('applyReplaceCover()');
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
});

test('Replace Cover reports ComicVine not answering, and an expired session goes to login', async t => {
    const page = await boot({ routes: { [CANDIDATES]: respond(500, { error: 'boom' }) } });
    t.after(() => page.close());

    await openReplaceCover(page);
    assert.equal(page.$('#replaceCoverList').textContent, 'ComicVine could not be asked: boom');

    page.route(CANDIDATES, 401);
    await page.run(`openReplaceCoverModal(${JSON.stringify(FILE)})`);
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
});

test('Closing Replace Cover drops a late answer, and nothing is applied without a choice', async t => {
    const answer = deferred();
    const page = await boot({ routes: { [CANDIDATES]: () => answer.promise } });
    t.after(() => page.close());

    await openReplaceCover(page);
    page.click(buttonByText(page, '#replaceCoverModal .modal-footer', 'Cancel'));
    answer.resolve(candidates());
    await page.settle();
    assert.ok(!isModalOpen(page, 'replaceCoverModal'));
    assert.equal(page.$$('#replaceCoverList .replace-cover-option').length, 0);

    await page.run('applyReplaceCover()');
    await page.run('selectReplaceCover(101)');
    assert.equal(callsTo(page, 'POST', '/api/covers/replace').length, 0);

    const failing = deferred();
    page.route(CANDIDATES, () => failing.promise);
    await openReplaceCover(page);
    await pressEscape(page);
    failing.resolve(respond(500, { error: 'late' }));
    await page.settle();
    assert.notEqual(page.$('#replaceCoverList').textContent, 'ComicVine could not be asked: late');
});

test('a cover that cannot be fetched again is still redrawn', async t => {
    const page = await boot({ routes: { [CANDIDATES]: candidates(), [REPLACE]: { replaced: true } } });
    t.after(() => page.close());
    page.route(`GET ${COVER}`, () => { throw new TypeError('Failed to fetch'); });

    await openReplaceCover(page);
    await page.run('applyReplaceCover()');

    assert.deepEqual(messageTexts(page, 'success'), ['Cover replaced']);
});

test('read-only users are not offered Replace Cover', async t => {
    const page = await boot({
        user: { canModifyLibrary: false, canAdminister: false, roles: ['ReadOnly'] },
        beforeScripts: addReadOnlyGate
    });
    t.after(() => page.close());

    const card = page.$$('.series-issue-card').find(element => element.dataset.filePath === FILE);
    page.click(card.querySelector('.dropdown-toggle'));
    const menu = page.document.getElementById(page.run(`getDropdownId(${JSON.stringify(FILE)})`));
    const item = [...menu.querySelectorAll('.dropdown-item')].find(button => button.textContent.trim() === 'Replace Cover…');
    assert.ok(item.hasAttribute('data-requires-write'));
});
