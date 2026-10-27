'use strict';

// Series library layouts (js/app/library.js): the list / grid / compact
// switch and its persistence, the automatic phone-portrait layout, the card
// markup of each layout and the (coerced) library view mode.

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    PORTRAIT_QUERY,
    makeSeries,
    makeSeriesList,
    bootLibrary,
    seriesCalls,
    savedPreferences,
    imageObserver,
    renderedTitles
} = require('./support/library-helpers');

const ISSUES_ROUTE = {
    'GET /api/files/series/series-1/issues': {
        issues: [{ file_path: 'Series 001/Series 001 #001.cbz', file_name: 'Series 001 #001.cbz', issue: '1' }],
        issue_count: 1
    }
};

const activeLayoutButtons = page => page.$$('#seriesLayoutToggle .header-view-btn.active, #seriesLayoutToolbarMobile .series-layout-mobile-btn.active')
    .map(button => button.id);

test('the layout buttons switch between list, grid and compact and remember the choice', async () => {
    const page = await bootLibrary({ series: makeSeriesList(2) });
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutCompactBtn', 'seriesLayoutCompactBtnMobile']);

    page.click('#seriesLayoutListBtn');
    const rows = page.$$('#fileList .series-list[role="list"] > .series-list-row');
    assert.equal(rows.length, 2);
    assert.equal(rows[0].getAttribute('aria-label'), 'Open series Series 001');
    assert.equal(rows[0].querySelector('.series-list-title').textContent, 'Series 001');
    assert.equal(rows[0].querySelector('.series-list-meta').textContent, '3.0 MB');
    assert.equal(rows[0].querySelector('.series-list-count').getAttribute('aria-label'), '3 issues');
    const thumb = rows[0].querySelector('img.series-list-thumb');
    assert.equal(thumb.dataset.protectedImage, 'Series 001/Series 001 #001.cbz');
    assert.ok(imageObserver(page).targets.has(thumb));
    assert.equal(page.window.localStorage.getItem('seriesLayout'), 'list');
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutListBtn', 'seriesLayoutListBtnMobile']);

    page.click('#seriesLayoutGridBtnMobile');
    const cards = page.$$('#fileList .series-grid--titled > .series-card--titled');
    assert.equal(cards.length, 2);
    assert.equal(cards[1].querySelector('.series-title--below').textContent, 'Series 002');
    assert.equal(cards[1].querySelector('.series-meta--below').textContent, '3.0 MB');
    assert.equal(cards[1].querySelector('.series-count-badge').textContent, '3');
    assert.equal(page.window.localStorage.getItem('seriesLayout'), 'grid');
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutGridBtn', 'seriesLayoutGridBtnMobile']);

    page.click('#seriesLayoutCompactBtn');
    assert.equal(page.$$('#fileList .series-grid--compact > .series-card').length, 2);
    assert.equal(page.window.localStorage.getItem('seriesLayout'), 'compact');
    // Switching layouts re-renders the loaded series; nothing is re-fetched.
    assert.equal(seriesCalls(page).length, 1);
    assert.deepEqual(page.errors, []);
});

test('a saved layout is used from the start', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1), localStorage: { seriesLayout: 'grid' } });

    assert.equal(page.$$('#fileList .series-grid--titled > .series-card--titled').length, 1);
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutGridBtn', 'seriesLayoutGridBtnMobile']);
});

test('opening the library from the overview marks the current layout', async () => {
    for (const [localStorage, expected] of [
        [{}, ['seriesLayoutCompactBtn', 'seriesLayoutCompactBtnMobile']],
        [{ seriesLayout: 'grid' }, ['seriesLayoutGridBtn', 'seriesLayoutGridBtnMobile']]
    ]) {
        // A visit without a route lands on the overview: the library is not shown yet.
        const page = await bootLibrary({ hash: '', series: makeSeriesList(1), localStorage });
        assert.equal(page.window.location.hash, '#/home');

        page.click('#navLibraryBtn');
        await page.settle();

        assert.equal(page.$('#seriesLayoutToggle').hidden, false);
        assert.equal(page.$('#seriesLayoutToolbarMobile').hidden, false);
        assert.deepEqual(activeLayoutButtons(page), expected, JSON.stringify(localStorage));
        page.close();
    }
});

test('a layout change made away from the library is marked when the library is shown again', async () => {
    const media = { [PORTRAIT_QUERY]: false };
    const page = await bootLibrary({ series: makeSeriesList(1), media });
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutCompactBtn', 'seriesLayoutCompactBtnMobile']);

    page.click('#navOverviewBtn');
    await page.settle();
    // The phone turns to portrait on the overview, so the automatic layout becomes the list.
    media[PORTRAIT_QUERY] = true;
    page.mediaQueries.filter(list => list.media === PORTRAIT_QUERY).forEach(list => list.change(true));
    page.click('#navLibraryBtn');
    await page.settle();

    assert.ok(page.$('#fileList .series-list'));
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutListBtn', 'seriesLayoutListBtnMobile']);
});

test('an unknown layout, saved or requested, is ignored', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1), localStorage: { seriesLayout: 'mosaic' } });
    assert.ok(page.$('#fileList .series-grid--compact'));
    const grid = page.$('#fileList .series-grid--compact');

    page.run("setSeriesLayout('mosaic')");

    assert.equal(page.$('#fileList .series-grid--compact'), grid);
    assert.equal(page.window.localStorage.getItem('seriesLayout'), 'mosaic');
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutCompactBtn', 'seriesLayoutCompactBtnMobile']);
});

test('the layout still switches when it cannot be saved', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1) });
    page.run("Storage.prototype.__setItem = Storage.prototype.setItem; Storage.prototype.setItem = () => { throw new Error('QuotaExceededError'); }");
    try {
        page.click('#seriesLayoutListBtn');
    } finally {
        page.run('Storage.prototype.setItem = Storage.prototype.__setItem; delete Storage.prototype.__setItem');
    }

    assert.equal(page.$$('#fileList .series-list > .series-list-row').length, 1);
    assert.equal(page.window.localStorage.getItem('seriesLayout'), null);
    assert.deepEqual(page.errors, []);
});

test('without a saved layout, phones in portrait get the list and rotating switches to compact', async () => {
    const media = { [PORTRAIT_QUERY]: true };
    const page = await bootLibrary({ series: makeSeriesList(2), media });
    assert.equal(page.$$('#fileList .series-list > .series-list-row').length, 2);
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutListBtn', 'seriesLayoutListBtnMobile']);

    media[PORTRAIT_QUERY] = false;
    page.mediaQueries.filter(list => list.media === PORTRAIT_QUERY).forEach(list => list.change(false));

    assert.equal(page.$$('#fileList .series-grid--compact > .series-card').length, 2);
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutCompactBtn', 'seriesLayoutCompactBtnMobile']);

    // Once the user picks a layout, rotating no longer changes it.
    page.click('#seriesLayoutGridBtn');
    media[PORTRAIT_QUERY] = true;
    page.mediaQueries.filter(list => list.media === PORTRAIT_QUERY).forEach(list => list.change(true));
    assert.ok(page.$('#fileList .series-grid--titled'));
    assert.equal(page.$('#fileList .series-list'), null);
});

test('the automatic layout falls back to compact where media queries fail', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1), media: { [PORTRAIT_QUERY]: true } });
    assert.ok(page.$('#fileList .series-list'));

    page.run("window.matchMedia = () => { throw new Error('matchMedia unsupported'); }");
    await page.run('loadSeriesLibrary()');

    assert.ok(page.$('#fileList .series-grid--compact'));
    assert.equal(page.$('#fileList .series-list'), null);
});

test('the layout toggle is hidden on a series page; a layout chosen there applies back in the library', async () => {
    const page = await bootLibrary({ hash: '#/series/series-1', series: makeSeriesList(2), routes: ISSUES_ROUTE });
    assert.equal(page.$('#seriesLayoutToggle').hidden, true);
    assert.equal(page.$('#seriesLayoutToolbarMobile').hidden, true);

    page.run("setSeriesLayout('list')");
    assert.equal(page.window.localStorage.getItem('seriesLayout'), 'list');
    // The series page is left alone.
    assert.ok(page.$('#seriesDetailPanel .series-issues-grid'));
    assert.equal(page.$('#fileList .series-list'), null);

    page.window.location.hash = '#/library';
    await page.settle();

    assert.equal(page.$('#seriesLayoutToggle').hidden, false);
    assert.deepEqual(activeLayoutButtons(page), ['seriesLayoutListBtn', 'seriesLayoutListBtnMobile']);
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
    assert.ok(page.$('#fileList .series-list'));
});

test('series with an external image use it as the cover and fall back to the first issue', async () => {
    const external = makeSeries(1, { has_external_image: true, external_image_url: '/api/series-images/abc' });
    // has_external_image without a URL keeps the first issue as the cover.
    const flagOnly = makeSeries(2, { has_external_image: true, external_image_url: null });

    for (const layout of ['compact', 'grid', 'list']) {
        const page = await bootLibrary({ series: [external, flagOnly], localStorage: { seriesLayout: layout } });
        const images = page.$$('#fileList img[data-protected-image]');

        assert.deepEqual(images.map(image => [image.dataset.protectedImage, image.dataset.protectedImageFallback]), [
            ['/api/series-images/abc', 'Series 001/Series 001 #001.cbz'],
            ['Series 002/Series 002 #001.cbz', '']
        ], layout);
        assert.equal(images[0].alt, 'Series 001 cover');
        page.close();
    }
});

test('titles are escaped in every layout', async () => {
    const tricky = makeSeries(1, { id: "it's-1", title: '<b>Bold</b> & "Co"' });

    for (const layout of ['compact', 'grid', 'list']) {
        const page = await bootLibrary({ series: [tricky], localStorage: { seriesLayout: layout } });

        assert.deepEqual(renderedTitles(page), ['<b>Bold</b> & "Co"'], layout);
        assert.equal(page.$('#fileList b'), null);
        const box = page.$('#fileList .series-select-box');
        assert.equal(box.dataset.seriesId, "it's-1");
        assert.equal(box.getAttribute('aria-label'), 'Select <b>Bold</b> & "Co"');
        page.close();
    }
});

test('each card shows the outcome of the last metadata lookup', async () => {
    const lastLookup = '2024-05-01T10:00:00Z';
    const series = [
        makeSeries(1, { lookup_status: 'success', metadata_source: 'ComicVine', last_lookup_utc: lastLookup }),
        makeSeries(2, { lookup_status: 'MANUAL_MATCH' }),
        makeSeries(3, { lookup_status: 'manual' }),
        makeSeries(4, { lookup_status: 'not_found', metadata_source: 'Metron' }),
        makeSeries(5, { lookup_status: 'error' }),
        makeSeries(6, { lookup_status: 'rate_limited' }),
        makeSeries(7)
    ];
    const page = await bootLibrary({ series });
    const localTime = page.evaluate(`new Date(${JSON.stringify(lastLookup)}).toLocaleString()`);

    const badges = page.$$('#fileList .series-lookup-badge').map(badge => ({
        cls: badge.className.replace('series-lookup-badge series-lookup-badge--', ''),
        symbol: badge.textContent,
        label: badge.getAttribute('aria-label'),
        title: badge.title
    }));
    assert.deepEqual(badges, [
        { cls: 'success', symbol: '✓', label: 'Provider matched', title: `Provider matched · ComicVine · ${localTime}` },
        { cls: 'success', symbol: '✓', label: 'Manually matched', title: 'Manually matched' },
        { cls: 'success', symbol: '✓', label: 'Manually matched', title: 'Manually matched' },
        { cls: 'warn', symbol: '?', label: 'No external match found', title: 'No external match found · Metron' },
        { cls: 'error', symbol: '!', label: 'Last lookup failed', title: 'Last lookup failed' },
        { cls: 'warn', symbol: '?', label: 'rate_limited', title: 'rate_limited' },
        { cls: 'unmatched', symbol: '✕', label: 'Not yet matched to a provider', title: 'Not yet matched to a provider' }
    ]);
});

test('the list layout shows the lookup badge beside the issue count', async () => {
    const page = await bootLibrary({
        series: [makeSeries(1, { lookup_status: 'error' })],
        localStorage: { seriesLayout: 'list' }
    });

    const aside = page.$('#fileList .series-list-row .series-list-aside');
    assert.ok(aside.querySelector('.series-lookup-badge--error'));
    assert.equal(aside.querySelector('.series-list-count').textContent, '3');
});

// ── Library view mode (always "series" now) ──────────────────────────────

test('requesting the removed Files view while browsing the library changes nothing', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1) });
    const before = seriesCalls(page).length;

    await page.run("setLibraryViewMode('files')");

    assert.equal(page.evaluate('libraryViewMode'), 'series');
    assert.equal(seriesCalls(page).length, before);
    assert.deepEqual(savedPreferences(page), []);
});

test('switching to the series view from a series page closes it and reloads the library', async () => {
    const page = await bootLibrary({ hash: '#/series/series-1', series: makeSeriesList(2), routes: ISSUES_ROUTE });
    // Legacy view-mode buttons, if a page still has them, follow the mode.
    page.$('.controls-wrapper').insertAdjacentHTML('beforeend',
        '<button id="filesViewModeBtn" class="active">Files</button><button id="seriesViewModeBtn">Series</button>');

    await page.run("setLibraryViewMode('series')");
    await page.settle();

    assert.equal(page.evaluate('currentSeriesDetailId'), null);
    assert.deepEqual(savedPreferences(page), [{ libraryViewMode: 'series' }]);
    assert.deepEqual(seriesCalls(page).at(-1).query, { offset: '0', limit: '60', refresh: 'true' });
    assert.deepEqual(renderedTitles(page), ['Series 001', 'Series 002']);
    assert.ok(page.$('#seriesViewModeBtn').classList.contains('active'));
    assert.ok(!page.$('#filesViewModeBtn').classList.contains('active'));
});

test('a legacy Files mode is coerced back to the series library', async () => {
    const page = await bootLibrary({ series: makeSeriesList(1) });
    page.run("libraryViewMode = 'files'");

    await page.run("setLibraryViewMode('files')");
    await page.settle();

    assert.equal(page.evaluate('libraryViewMode'), 'series');
    assert.deepEqual(savedPreferences(page), [{ libraryViewMode: 'series' }]);
    assert.equal(seriesCalls(page).at(-1).query.refresh, 'true');
    assert.deepEqual(renderedTitles(page), ['Series 001']);
});
