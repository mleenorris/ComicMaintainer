'use strict';

// js/app/series.js: the series' volumes and their ComicVine links, listed
// under the synopsis (GET /api/metadata/volumes/series/{id}).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { bootSeriesPage, seriesSummary, seriesIssue, callsTo } = require('./support/email-helpers');

const SERIES = seriesSummary({ id: 'all-new-x-men', title: 'All-New X-Men', issue_count: 2 });
const VOLUMES = 'GET /api/metadata/volumes/series/all-new-x-men';

function boot(options = {}) {
    return bootSeriesPage({ series: SERIES, issues: [seriesIssue()], ...options });
}

function items(page) {
    return page.$$('.series-volume-links li').map(item => item.textContent.replace(/\s+/g, ' ').trim());
}

const LINKED = {
    volumes: [
        { volume_number: 1, start_year: 2013, issues: 41, comicvine_volume_id: 53919, name: 'All-New X-Men', publisher: 'Marvel', site_url: 'https://comicvine.gamespot.com/volume/4050-53919/' },
        { volume_number: 2, start_year: null, issues: 1, comicvine_volume_id: 88888, name: null, publisher: null, site_url: 'https://comicvine.gamespot.com/volume/4050-88888/' },
        { volume_number: null, start_year: 2015, issues: 5, comicvine_volume_id: null, name: null, publisher: null, site_url: null },
        { volume_number: null, start_year: null, issues: 2, comicvine_volume_id: 1, name: 'Bad', publisher: null, site_url: 'javascript:alert(1)' }
    ]
};

test('the series lists each volume with its ComicVine link', async t => {
    const page = await boot({ routes: { [VOLUMES]: LINKED } });
    t.after(() => page.close());
    await page.settle();

    assert.deepEqual(items(page), [
        'Vol. 1 (2013) · 41 issues · All-New X-Men on ComicVine (Marvel)',
        'Vol. 2 · 1 issue · ComicVine on ComicVine',
        '(2015) · 5 issues · not linked to ComicVine yet',
        'No volume · 2 issues · not linked to ComicVine yet'
    ]);
    const links = page.$$('.series-volume-links a');
    assert.deepEqual(links.map(link => link.getAttribute('href')), [
        'https://comicvine.gamespot.com/volume/4050-53919/',
        'https://comicvine.gamespot.com/volume/4050-88888/'
    ]);
    assert.equal(links[0].getAttribute('target'), '_blank');
});

test('a series with no linked volume, or whose volumes cannot be read, lists none', async t => {
    const unlinked = await boot({ routes: { [VOLUMES]: { volumes: [{ volume_number: 1, issues: 3, site_url: null }] } } });
    t.after(() => unlinked.close());
    await unlinked.settle();
    assert.deepEqual(items(unlinked), []);

    const none = await boot({ routes: { [VOLUMES]: { volumes: null } } });
    t.after(() => none.close());
    await none.settle();
    assert.deepEqual(items(none), []);

    const failing = await boot({ routes: { [VOLUMES]: respond(500, { error: 'boom' }) } });
    t.after(() => failing.close());
    await failing.settle();
    assert.deepEqual(items(failing), []);

    const expired = await boot({ routes: { [VOLUMES]: 401 } });
    t.after(() => expired.close());
    await expired.settle();
    assert.deepEqual(items(expired), []);
});

test('the volumes are read again when the issues reload, and a stale answer is dropped', async t => {
    const answers = [deferred(), deferred()];
    let call = 0;
    const page = await boot({ routes: { [VOLUMES]: () => answers[call++].promise } });
    t.after(() => page.close());
    await page.settle();

    await page.run('loadSeriesIssues("all-new-x-men", true, { immediate: true })');
    await page.settle();
    answers[0].resolve({ volumes: [{ volume_number: 9, issues: 1, site_url: 'https://comicvine.gamespot.com/volume/4050-9/' }] });
    await page.settle();
    assert.deepEqual(items(page), []);

    answers[1].resolve(LINKED);
    await page.settle();
    assert.equal(callsTo(page, 'GET', '/api/metadata/volumes/series/all-new-x-men').length, 2);
    assert.equal(items(page).length, 4);
});
