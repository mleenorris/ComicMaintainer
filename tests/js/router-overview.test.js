'use strict';

// js/app/router.js — the overview (home) page: loading state, the three rows
// built from GET /api/overview (OverviewService.OverviewResult), card
// contents, where cards / Resume / "See all" lead, and failure + Retry.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const { boot, requests, consoleLines, overview, seriesCard, seriesIssues, seriesPage } = require('./support/core-helpers');

const SAGA = seriesCard();
const MONSTRESS = seriesCard({
    id: 'series-2', title: 'Monstress', canonical_title: 'Monstress', issue_count: 12, total_size: 512 * 1024,
    cover_file_path: 'Monstress/Monstress 001.cbz', has_external_image: true,
    external_image_url: 'https://covers.example/monstress.jpg', lookup_status: 'success'
});
const PAPER_GIRLS = seriesCard({
    id: 'series 3/x', title: 'Paper "Girls" & Co', canonical_title: 'Paper Girls', issue_count: 1, total_size: 2 * 1024 * 1024 * 1024,
    cover_file_path: 'Paper Girls/PG 001.cbz', has_external_image: true, external_image_url: null, lookup_status: 'not_found'
});
const inProgress = (series, file) => ({ ...series, resume_file_path: file, resume_page: 4, last_read_utc: '2024-05-01T10:00:00Z' });

const rowTitles = page => [...page.$$('#overviewRows .overview-row-title')].map(title => title.textContent.trim());
const rowCards = (page, index) => [...page.$$('#overviewRows .overview-row')[index].querySelectorAll('.overview-card')];
const emptyTexts = page => [...page.$$('#overviewRows .overview-empty')].map(empty => empty.textContent);
const fragmentNavigations = page => page.navigations.filter(nav => !nav.url.startsWith('http://localhost/index.html#'));

test('the overview shows a loading state until its data arrives, then one row per list', async () => {
    const pending = deferred();
    const page = await boot({ hash: '#/home', routes: { 'GET /api/overview': () => pending.promise } });

    assert.equal(page.$('#overviewRows .loading').textContent.trim(), 'Loading overview...');
    assert.ok(page.$('#overviewRows .spinner'));

    pending.resolve(overview({
        continue_reading: [inProgress(SAGA, 'Saga/Saga 002.cbz')],
        series_updates: [SAGA, MONSTRESS],
        newly_added_series: [PAPER_GIRLS]
    }));
    await page.settle();

    assert.equal(page.$('#overviewRows .loading'), null);
    assert.deepEqual(rowTitles(page), ['Continue Reading', 'Series Updates', 'Newly Added Series']);
    assert.deepEqual([0, 1, 2].map(index => rowCards(page, index).map(card => card.querySelector('.series-title').textContent)),
        [['Saga'], ['Saga', 'Monstress'], ['Paper "Girls" & Co']]);
    assert.deepEqual(emptyTexts(page), []);
    assert.equal(page.$$('#overviewRows .overview-see-all').length, 2, 'only the update rows offer "See all"');
    assert.equal(page.$$('#overviewRows .overview-row')[0].querySelector('.overview-see-all'), null);
});

test('empty rows say why they are empty and offer no "See all"', async () => {
    const page = await boot({ hash: '#/home' });

    assert.deepEqual(rowTitles(page), ['Continue Reading', 'Series Updates', 'Newly Added Series']);
    assert.deepEqual(emptyTexts(page), ['Nothing in progress yet.', 'No new files in the last 30 days.', 'No new series in the last 30 days.']);
    assert.equal(page.$('#overviewRows .overview-see-all'), null);
    assert.equal(page.$('#overviewRows .overview-card'), null);
});

test('a response without the lists renders every row empty', async () => {
    const page = await boot({ hash: '#/home', routes: { 'GET /api/overview': {} } });

    assert.deepEqual(emptyTexts(page), ['Nothing in progress yet.', 'No new files in the last 30 days.', 'No new series in the last 30 days.']);
});

test('cards show the cover, issue count, size and provider match of each series', async () => {
    const page = await boot({
        hash: '#/home',
        routes: { 'GET /api/overview': overview({ series_updates: [SAGA, MONSTRESS, PAPER_GIRLS] }) }
    });
    const cards = rowCards(page, 1).map(card => {
        const image = card.querySelector('img.series-cover');
        const badge = card.querySelector('.series-lookup-badge');
        return {
            label: card.querySelector('.overview-series-card').getAttribute('aria-label'),
            title: card.querySelector('.series-title').getAttribute('title'),
            count: card.querySelector('.series-count-badge').textContent,
            size: card.querySelector('.series-meta').textContent,
            cover: image.dataset.protectedImage,
            fallback: image.dataset.protectedImageFallback,
            alt: image.alt,
            lookup: [badge.className, badge.getAttribute('aria-label')]
        };
    });

    assert.deepEqual(cards, [
        {
            label: 'Open series Saga', title: 'Saga', count: '3', size: '3.0 MB',
            cover: 'Saga/Saga 001.cbz', fallback: '', alt: 'Saga cover',
            lookup: ['series-lookup-badge series-lookup-badge--unmatched', 'Not yet matched to a provider']
        },
        {
            label: 'Open series Monstress', title: 'Monstress', count: '12', size: '512.0 KB',
            cover: 'https://covers.example/monstress.jpg', fallback: 'Monstress/Monstress 001.cbz', alt: 'Monstress cover',
            lookup: ['series-lookup-badge series-lookup-badge--success', 'Provider matched']
        },
        {
            label: 'Open series Paper "Girls" & Co', title: 'Paper "Girls" & Co', count: '1', size: '2.0 GB',
            cover: 'Paper Girls/PG 001.cbz', fallback: '', alt: 'Paper "Girls" & Co cover',
            lookup: ['series-lookup-badge series-lookup-badge--warn', 'No external match found']
        }
    ]);
    assert.equal(page.$('#overviewRows .overview-resume-btn'), null, 'only Continue Reading offers Resume');
});

test('opening a card shows that series, and its Back button returns to the overview', async () => {
    const page = await boot({
        hash: '#/home',
        routes: {
            'GET /api/overview': overview({ newly_added_series: [PAPER_GIRLS] }),
            'GET /api/files/series': seriesPage([PAPER_GIRLS]),
            [`GET /api/files/series/${encodeURIComponent(PAPER_GIRLS.id)}/issues`]: seriesIssues(PAPER_GIRLS)
        }
    });

    page.click('#overviewRows .overview-series-card');
    await page.settle();

    assert.equal(page.window.location.hash, '#/series/series%203%2Fx');
    assert.equal(page.evaluate('currentView'), 'series');
    assert.equal(page.evaluate('currentSeriesDetailId'), PAPER_GIRLS.id);
    assert.equal(page.evaluate('seriesDetailOrigin'), 'home');

    page.click('#seriesDetailPanel .series-detail-back');
    await page.settle();
    assert.equal(page.window.location.hash, '#/home');
    assert.equal(page.evaluate('currentView'), 'home');
});

test('Resume opens the reader at the file in progress without opening the series', async () => {
    const page = await boot({
        hash: '#/home',
        routes: {
            'GET /api/overview': overview({
                continue_reading: [inProgress(SAGA, "Saga/Saga 'Two' #2.cbz"), MONSTRESS]
            })
        }
    });
    const cards = rowCards(page, 0);
    assert.ok(cards[0].querySelector('.overview-resume-btn'));
    assert.equal(cards[1].querySelector('.overview-resume-btn'), null, 'nothing to resume without a file in progress');
    assert.match(cards[0].querySelector('.overview-resume-btn').textContent, /Resume/);

    page.click(cards[0].querySelector('.overview-resume-btn'));
    await page.settle();

    const opened = fragmentNavigations(page).map(nav => {
        const url = new URL(nav.url);
        return { page: url.pathname, file: url.searchParams.get('file'), replace: nav.replace };
    });
    assert.deepEqual(opened, [{ page: '/reader.html', file: "Saga/Saga 'Two' #2.cbz", replace: false }]);
    assert.equal(page.window.location.hash, '#/home', 'the card itself was not opened');
    assert.equal(page.evaluate('currentView'), 'home');
});

test('"See all" opens the library', async () => {
    const page = await boot({
        hash: '#/home',
        routes: { 'GET /api/overview': overview({ series_updates: [SAGA] }), 'GET /api/files/series': seriesPage([SAGA]) }
    });

    page.click('#overviewRows .overview-see-all');
    await page.settle();

    assert.equal(page.window.location.hash, '#/library');
    assert.equal(page.evaluate('currentView'), 'library');
});

test('a failed overview explains itself and Retry loads it again', async () => {
    for (const failure of [respond(500, { error: 'boom' }), () => { throw new TypeError('Failed to fetch'); }]) {
        const page = await boot({ hash: '#/home', routes: { 'GET /api/overview': failure } });

        assert.equal(page.$('#overviewRows .empty-state p').firstChild.textContent, "Couldn't load your overview. ");
        assert.equal(consoleLines(page, 'error').filter(line => line.startsWith('Failed to load overview')).length, 1);

        page.route('GET /api/overview', overview({ series_updates: [SAGA] }));
        page.click('#overviewRows .empty-state button');
        assert.equal(page.$('#overviewRows .loading').textContent.trim(), 'Loading overview...', 'nothing was shown yet');
        await page.settle();

        assert.equal(page.$('#overviewRows .empty-state'), null);
        assert.deepEqual(rowCards(page, 1).map(card => card.querySelector('.series-title').textContent), ['Saga']);
    }
});

test('coming back to the overview keeps the last rows on screen while they refresh', async () => {
    const page = await boot({
        hash: '#/home',
        routes: { 'GET /api/overview': overview({ series_updates: [SAGA] }), 'GET /api/files/series': seriesPage([SAGA]) }
    });
    page.run("navigate('#/library')");
    await page.settle();

    const pending = deferred();
    page.route('GET /api/overview', () => pending.promise);
    page.run("navigate('#/home')");
    await page.settle();
    assert.equal(page.$('#overviewRows .loading'), null);
    assert.deepEqual(rowCards(page, 1).map(card => card.querySelector('.series-title').textContent), ['Saga']);

    pending.resolve(overview({ series_updates: [MONSTRESS, SAGA] }));
    await page.settle();
    assert.deepEqual(rowCards(page, 1).map(card => card.querySelector('.series-title').textContent), ['Monstress', 'Saga']);
});

test('the overview request is authenticated and same-origin', async () => {
    const page = await boot({ hash: '#/home' });
    const [request] = requests(page, 'GET', '/api/overview');

    assert.match(request.headers.authorization, /^Bearer /);
    assert.equal(request.init.credentials, 'same-origin');

    // Without the auth helper (api.js not loaded) the request still goes out.
    page.run('{ const helper = getAuthHeaders; getAuthHeaders = undefined; renderOverview(); getAuthHeaders = helper; }');
    await page.settle();
    const last = requests(page, 'GET', '/api/overview').at(-1);
    assert.equal(last.headers.authorization, undefined);
    assert.equal(last.init.headers, undefined);
});

test('nothing is fetched when the page has no overview section', async () => {
    const page = await boot({ hash: '#/library' });
    page.$('#overviewRows').remove();
    const before = requests(page, 'GET', '/api/overview').length;

    page.run("navigate('#/home')");
    await page.settle();

    assert.equal(page.evaluate('currentView'), 'home');
    assert.equal(requests(page, 'GET', '/api/overview').length, before);
});
