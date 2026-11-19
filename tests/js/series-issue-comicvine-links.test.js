'use strict';

// js/app/series.js: each issue's ComicVine page, on its card and in its menu
// (GET /api/metadata/comicvine-links/series/{id}).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { bootSeriesPage, seriesSummary, seriesIssue, callsTo } = require('./support/email-helpers');

const SERIES = seriesSummary({ id: 'batman', title: 'Batman', issue_count: 3 });
const LINKS = 'GET /api/metadata/comicvine-links/series/batman';
const ISSUES = [
    seriesIssue({ file_path: '/c/Batman/1.cbz', file_name: '1.cbz', issue: '1' }),
    seriesIssue({ file_path: '/c/Batman/2.cbz', file_name: '2.cbz', issue: '2' }),
    seriesIssue({ file_path: '/c/Batman/3.cbz', file_name: '3.cbz', issue: '3' })
];

const ANSWER = {
    issues: [
        { file_path: '/c/Batman/1.cbz', issue_id: 101, issue_number: '1', issue_name: 'I Am Gotham', issue_url: 'https://comicvine.gamespot.com/batman-1/4000-101/', volume_id: 7, volume_name: 'Batman', volume_url: 'https://comicvine.gamespot.com/volume/4050-7/', filled: true },
        { file_path: '/c/Batman/2.cbz', issue_id: null, issue_number: null, issue_name: null, issue_url: null, volume_id: 7, volume_name: null, volume_url: 'https://comicvine.gamespot.com/volume/4050-7/', filled: false },
        { file_path: '/c/Batman/3.cbz', issue_id: 103, issue_number: '3', issue_name: null, issue_url: 'javascript:alert(1)', volume_id: 7, volume_name: 'Batman', volume_url: 'javascript:alert(1)', filled: false },
        null
    ]
};

function boot(options = {}) {
    return bootSeriesPage({ series: SERIES, issues: ISSUES, ...options });
}

function cardLink(page, filePath) {
    const card = page.$$('.series-issue-card').find(item => item.getAttribute('data-file-path') === filePath);
    return card ? card.querySelector('.series-issue-comicvine') : null;
}

async function menuLink(page, filePath) {
    await page.run(`fillSeriesIssueMenu(${JSON.stringify(filePath)})`);
    const card = page.$$('.series-issue-card').find(item => item.getAttribute('data-file-path') === filePath);
    return [...card.querySelectorAll('.dropdown-menu a.dropdown-item')].find(item => /ComicVine/.test(item.textContent)) || null;
}

test('an issue links to its ComicVine page, else to its volume', async t => {
    const page = await boot({ routes: { [LINKS]: ANSWER } });
    t.after(() => page.close());
    await page.settle();

    const first = cardLink(page, '/c/Batman/1.cbz');
    assert.equal(first.textContent, 'ComicVine');
    assert.equal(first.getAttribute('href'), 'https://comicvine.gamespot.com/batman-1/4000-101/');
    assert.equal(first.getAttribute('target'), '_blank');
    assert.equal(first.getAttribute('title'), '#1 I Am Gotham on ComicVine (Batman)');

    const second = cardLink(page, '/c/Batman/2.cbz');
    assert.equal(second.textContent, 'Volume on ComicVine');
    assert.equal(second.getAttribute('href'), 'https://comicvine.gamespot.com/volume/4050-7/');
    assert.equal(second.getAttribute('title'), 'Its volume on ComicVine; this issue is not in its listing yet');

    // Only http(s) links are shown.
    assert.equal(cardLink(page, '/c/Batman/3.cbz'), null);

    const item = await menuLink(page, '/c/Batman/1.cbz');
    assert.match(item.textContent, /Open on ComicVine/);
    assert.equal(item.getAttribute('href'), 'https://comicvine.gamespot.com/batman-1/4000-101/');
    assert.match((await menuLink(page, '/c/Batman/2.cbz')).textContent, /Open Volume on ComicVine/);
    assert.equal(await menuLink(page, '/c/Batman/3.cbz'), null);
});

test('an issue matched only by its number says so', async t => {
    const answer = { issues: [{ ...ANSWER.issues[0], issue_name: null, filled: false }] };
    const page = await boot({ routes: { [LINKS]: answer } });
    t.after(() => page.close());
    await page.settle();

    assert.equal(cardLink(page, '/c/Batman/1.cbz').getAttribute('title'), '#1 on ComicVine (Batman, matched by its number)');
});

test('no link shows when there is none, or the links cannot be read', async t => {
    for (const answer of [{ issues: [] }, { issues: null }, respond(500, { error: 'boom' }), 401]) {
        const page = await boot({ routes: { [LINKS]: answer } });
        await page.settle();
        assert.equal(page.$$('.series-issue-comicvine').length, 0);
        assert.equal(await menuLink(page, '/c/Batman/1.cbz'), null);
        page.close();
    }
});

test('the links are read again when the issues reload, and a stale answer is dropped', async t => {
    const answers = [deferred(), deferred()];
    let call = 0;
    const page = await boot({ routes: { [LINKS]: () => answers[call++].promise } });
    t.after(() => page.close());
    await page.settle();

    await page.run('loadSeriesIssues("batman", true, { immediate: true })');
    await page.settle();
    answers[0].resolve(ANSWER);
    await page.settle();
    assert.equal(page.$$('.series-issue-comicvine').length, 0);

    answers[1].resolve(ANSWER);
    await page.settle();
    assert.equal(callsTo(page, 'GET', '/api/metadata/comicvine-links/series/batman').length, 2);
    assert.equal(page.$$('.series-issue-comicvine').length, 2);
});
