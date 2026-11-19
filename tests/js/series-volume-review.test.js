'use strict';

// js/app/series.js: a series volume's ComicVine volume left for the user to review, the
// evidence for each candidate, and the user's choice
// (POST /api/metadata/volumes/series/{id}/choose).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { bootSeriesPage, seriesSummary, seriesIssue, callsTo, messageTexts, isModalOpen } = require('./support/email-helpers');

const SERIES = seriesSummary({ id: 'batman', title: 'Batman', issue_count: 2 });
const VOLUMES = 'GET /api/metadata/volumes/series/batman';
const CHOOSE = 'POST /api/metadata/volumes/series/batman/choose';

const candidate = (id, name, startYear, reasons) => ({
    volume_id: id, name, start_year: startYear, publisher: 'DC Comics', issues: 158, score: 5, reasons,
    site_url: `https://comicvine.gamespot.com/volume/4050-${id}/`
});

const REVIEW = {
    volumes: [
        {
            volume_number: null, start_year: 2016, issues: 2, comicvine_volume_id: null, name: null, publisher: null, site_url: null,
            series_key: 'batman', searched: true, decision: 'review', review_reason: '2 ComicVine volumes named Batman fit about equally well',
            candidates: [candidate(91273, 'Batman', 2016, ['started in 2016', 'published by DC Comics, as your issues say']), candidate(5555, 'Batman', 2016, ['started in 2016'])],
            conflicts: [], mixed_years: []
        },
        {
            volume_number: 1, start_year: null, issues: 3, comicvine_volume_id: 796, name: 'Batman', publisher: 'DC Comics',
            site_url: 'https://comicvine.gamespot.com/volume/4050-796/', decision: 'manual', review_reason: null,
            candidates: [], conflicts: [{ file_path: '/c/a.cbz', issue: '3', year: 2010, comicvine_year: 1940 }], mixed_years: []
        },
        {
            volume_number: null, start_year: null, issues: 4, comicvine_volume_id: null, name: null, publisher: null, site_url: null,
            decision: 'manual', review_reason: null, candidates: null, conflicts: null, mixed_years: [2011, 2016]
        }
    ]
};

function type(page, input, value) {
    input.value = value;
    input.dispatchEvent(new page.window.Event('input', { bubbles: true }));
}

function boot(routes) {
    return bootSeriesPage({ series: SERIES, issues: [seriesIssue()], routes });
}

function items(page) {
    return page.$$('.series-volume-links li').map(item => item.textContent.replace(/\s+/g, ' ').trim());
}

async function openChoose(page, index) {
    page.click(page.$$('.series-volume-links li')[index].querySelector('button'));
    await page.settle();
}

function options(page) {
    return page.$$('#chooseVolumeList .choose-volume-option').map(option => ({
        text: option.querySelector('.choose-volume-pick').textContent.replace(/\s+/g, ' ').trim(),
        selected: option.classList.contains('choose-volume-option--selected'),
        reasons: [...option.querySelectorAll('.choose-volume-reasons li')].map(item => item.textContent)
    }));
}

test('a volume left for review, a chosen one, and issues dated or filed wrongly show on the series page', async t => {
    const page = await boot({ [VOLUMES]: REVIEW });
    t.after(() => page.close());
    await page.settle();

    assert.deepEqual(items(page), [
        '(2016) · 2 issues · needs review: 2 ComicVine volumes named Batman fit about equally well Choose Volume…',
        'Vol. 1 · 3 issues · Batman on ComicVine (DC Comics) · your choice Change… · 1 issue dated years from ComicVine\'s',
        'No volume · 4 issues · no ComicVine volume, as you chose · your choice Change… · issue numbers repeat across 2011, 2016: these may be several volumes; set their volume years'
    ]);
    assert.equal(page.$('.series-volume-conflict').getAttribute('title'), '#3: yours 2010, ComicVine\'s 1940');
});

test('Choose Volume lists the volumes weighed, with their evidence, and sets the one chosen', async t => {
    const page = await boot({ [VOLUMES]: REVIEW, [CHOOSE]: { success: true } });
    t.after(() => page.close());
    await page.settle();

    await openChoose(page, 0);
    assert.ok(isModalOpen(page, 'chooseVolumeModal'));
    assert.equal(page.$('#chooseVolumeNote').textContent, '2 ComicVine volumes named Batman fit about equally well. Which is (2016)?');
    assert.deepEqual(options(page), [
        { text: 'Batman started 2016 · DC Comics · 158 issues', selected: false, reasons: ['started in 2016', 'published by DC Comics, as your issues say'] },
        { text: 'Batman started 2016 · DC Comics · 158 issues', selected: false, reasons: ['started in 2016'] }
    ]);
    assert.ok(page.$('#chooseVolumeApplyBtn').disabled);

    page.click(page.$$('.choose-volume-pick')[1]);
    assert.deepEqual(options(page).map(option => option.selected), [false, true]);
    assert.ok(!page.$('#chooseVolumeApplyBtn').disabled);
    page.click(page.$('#chooseVolumeApplyBtn'));
    await page.settle();

    const [call] = callsTo(page, 'POST', '/api/metadata/volumes/series/batman/choose');
    assert.deepEqual(JSON.parse(JSON.stringify(call.body)), { series_key: 'batman', start_year: 2016, volume_number: 0, volume_id: 5555 });
    assert.ok(!isModalOpen(page, 'chooseVolumeModal'));
    assert.deepEqual(messageTexts(page, 'success'), ['Volume chosen: run Fetch Issue Details to fill its issues']);
    // The volumes are read again.
    assert.equal(callsTo(page, 'GET', '/api/metadata/volumes/series/batman').length, 2);
});

test('another volume can be given by its ComicVine link or id, or none chosen', async t => {
    const page = await boot({ [VOLUMES]: REVIEW, [CHOOSE]: { success: true } });
    t.after(() => page.close());
    await page.settle();

    // A volume chosen before is preselected; typing a link replaces it.
    await openChoose(page, 1);
    assert.equal(page.$('#chooseVolumeList').textContent, 'No volumes were weighed for it: paste the link of its ComicVine volume.');
    assert.ok(!page.$('#chooseVolumeApplyBtn').disabled);
    type(page, page.$('#chooseVolumeLink'), 'not a link');
    assert.ok(page.$('#chooseVolumeApplyBtn').disabled);
    type(page, page.$('#chooseVolumeLink'), 'https://comicvine.gamespot.com/batman/4050-42721/');
    assert.ok(!page.$('#chooseVolumeApplyBtn').disabled);
    page.click(page.$('#chooseVolumeApplyBtn'));
    await page.settle();
    assert.deepEqual(JSON.parse(JSON.stringify(callsTo(page, 'POST', '/api/metadata/volumes/series/batman/choose')[0].body)),
        { series_key: null, start_year: 0, volume_number: 1, volume_id: 42721 });

    await openChoose(page, 2);
    type(page, page.$('#chooseVolumeLink'), '796');
    page.click(page.$$('#chooseVolumeModal .modal-footer .btn')[1]);
    await page.settle();
    assert.deepEqual(JSON.parse(JSON.stringify(callsTo(page, 'POST', '/api/metadata/volumes/series/batman/choose')[1].body)),
        { series_key: null, start_year: 0, volume_number: 0, volume_id: null });
    assert.deepEqual(messageTexts(page, 'success').slice(-1), ['The volume is kept off ComicVine']);
});

test('a choice that fails says so and keeps the dialog open; Cancel closes it', async t => {
    const page = await boot({ [VOLUMES]: REVIEW, [CHOOSE]: respond(500, { error: 'boom' }) });
    t.after(() => page.close());
    await page.settle();

    await openChoose(page, 0);
    page.click(page.$$('.choose-volume-pick')[0]);
    page.click(page.$('#chooseVolumeApplyBtn'));
    await page.settle();
    assert.ok(isModalOpen(page, 'chooseVolumeModal'));
    assert.equal(messageTexts(page, 'error').length, 1);
    assert.match(messageTexts(page, 'error')[0], /^Failed to choose the volume/);

    page.click(page.$$('#chooseVolumeModal .modal-footer .btn')[2]);
    assert.ok(!isModalOpen(page, 'chooseVolumeModal'));
    // Nothing happens without a dialog.
    await page.run('applyChooseVolume(false)');
    page.run('selectChooseVolume(1); chooseVolumeLinkTyped()');
    assert.equal(callsTo(page, 'POST', '/api/metadata/volumes/series/batman/choose').length, 1);
});

test('a volume looked for and not found offers a choice; one never looked for shows nothing', async t => {
    const page = await boot({ [VOLUMES]: { volumes: [
        { volume_number: 2, issues: 3, site_url: null, searched: true, decision: null, candidates: [], conflicts: [], mixed_years: [] },
        { volume_number: 3, issues: 1, site_url: null, searched: false, decision: null, candidates: [candidate(7, 'Batman', 2012, ['started in 2012, a year off 2011'])], conflicts: [], mixed_years: [] },
        { volume_number: 4, issues: 1, site_url: null, searched: false, decision: null, candidates: [], conflicts: [], mixed_years: [] }
    ] } });
    t.after(() => page.close());
    await page.settle();
    assert.deepEqual(items(page), [
        'Vol. 2 · 3 issues · not found on ComicVine Choose Volume…',
        'Vol. 3 · 1 issue · not linked to ComicVine yet Choose Volume…',
        'Vol. 4 · 1 issue · not linked to ComicVine yet'
    ]);
});

test('a series with only volumes that are fine and unlinked lists none, and a missing volume opens nothing', async t => {
    const page = await boot({ [VOLUMES]: { volumes: [{ volume_number: 1, issues: 3, site_url: null, decision: null, candidates: [], conflicts: [], mixed_years: [] }] } });
    t.after(() => page.close());
    await page.settle();
    assert.deepEqual(items(page), []);
    page.run('openChooseVolumeModal(5)');
    assert.ok(!isModalOpen(page, 'chooseVolumeModal'));
});
