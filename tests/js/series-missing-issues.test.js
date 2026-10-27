'use strict';

// Missing issues on the series page (js/app/series.js): issue ordering
// (decimals between integers, non-numeric issues last), the placeholders for
// the integer issues the series lacks, and the banner summarising them.

const test = require('node:test');
const assert = require('node:assert/strict');
const { bootSeries, issue, issueCards, text } = require('./support/series-helpers');

// Each grid entry as "#N missing" for a placeholder or the issue's file path.
const layout = page => issueCards(page).map(card => (card.classList.contains('series-issue-card--missing')
    ? `${card.querySelector('.series-issue-badge').textContent} missing`
    : card.dataset.filePath));
const banner = page => page.$('#seriesDetailPanel .series-detail-missing-banner');
const bannerText = page => text(page.$('#seriesDetailPanel .series-detail-missing-banner-text'));
// An issue with a free-form (or no) issue number.
const special = (number, name) => issue(0, { issue: number, file_path: `/comics/Saga/${name}.cbz`, file_name: `${name}.cbz`, title: name });

test('issues are ordered by number with the missing integers in place and non-numeric issues last', async () => {
    const page = await bootSeries({
        issues: [
            issue(3),
            special('Annual', 'Saga Annual'),
            issue('1.5'),
            special(' 001 ', 'Saga 001 (Variant)'),
            issue(5),
            special('', 'Saga Special'),
            special('1a', 'Saga 1a'),
            special('0', 'Saga 000'),
            special(null, 'Saga Sketchbook')
        ]
    });

    assert.deepEqual(layout(page), [
        '/comics/Saga/Saga 000.cbz',
        '/comics/Saga/Saga 001 (Variant).cbz',
        '/comics/Saga/Saga 1.5.cbz',
        '#2 missing',
        '/comics/Saga/Saga 003.cbz',
        '#4 missing',
        '/comics/Saga/Saga 005.cbz',
        '/comics/Saga/Saga Annual.cbz',
        '/comics/Saga/Saga Special.cbz',
        '/comics/Saga/Saga 1a.cbz',
        '/comics/Saga/Saga Sketchbook.cbz'
    ]);
    assert.equal(bannerText(page), 'Missing 2 issues: #2, #4');
    assert.equal(banner(page).getAttribute('role'), 'status');

    // Issues without a number carry no badge; the others show theirs as given.
    const badges = issueCards(page).map(card => card.querySelector('.series-issue-badge')?.textContent ?? null);
    assert.deepEqual(badges, ['#0', '# 001 ', '#1.5', '#2', '#3', '#4', '#5', '#Annual', null, '#1a', null]);
});

test('issues sharing a number, such as variants, keep the order the server sent them in', async () => {
    const page = await bootSeries({
        issues: [issue(2), special('1', 'Saga 001 (Variant)'), issue(1), special('2', 'Saga 002 (Director\'s Cut)')]
    });

    assert.deepEqual(layout(page), [
        '/comics/Saga/Saga 001 (Variant).cbz',
        '/comics/Saga/Saga 001.cbz',
        '/comics/Saga/Saga 002.cbz',
        '/comics/Saga/Saga 002 (Director\'s Cut).cbz'
    ]);
    assert.equal(banner(page), null);
});

test('a missing issue placeholder says which issue is not in the collection', async () => {
    const page = await bootSeries({ issues: [issue(1), issue(3)] });

    const placeholder = page.$('#seriesDetailPanel .series-issue-card--missing');
    assert.equal(placeholder.getAttribute('aria-label'), 'Missing issue #2');
    assert.equal(placeholder.getAttribute('title'), 'Missing issue #2');
    assert.equal(text(placeholder.querySelector('.series-issue-title')), 'Missing');
    assert.equal(text(placeholder.querySelector('.series-issue-subtitle')), 'Issue #2 is not in your collection');
    assert.equal(placeholder.querySelector('input[type="checkbox"]'), null, 'a placeholder cannot be selected');
    assert.equal(placeholder.dataset.filePath, undefined);
    assert.equal(bannerText(page), 'Missing 1 issue: #2');
    assert.equal(banner(page).querySelector('button'), null, 'no download action without Suwayomi');
});

test('runs of missing issues are summarised as ranges', async () => {
    const page = await bootSeries({ issues: [issue(1), issue(5), issue(7), issue(8), issue(12)] });

    assert.equal(bannerText(page), 'Missing 7 issues: #2-4, #6, #9-11');
    assert.deepEqual(page.$$('#seriesDetailPanel .series-issue-card--missing').map(card => card.getAttribute('aria-label')), [
        'Missing issue #2', 'Missing issue #3', 'Missing issue #4', 'Missing issue #6',
        'Missing issue #9', 'Missing issue #10', 'Missing issue #11'
    ]);
    assert.equal(issueCards(page).length, 12);
});

test('a series that starts after #1 reports the run before it', async () => {
    const page = await bootSeries({ issues: [issue(3), issue(4)] });

    assert.equal(bannerText(page), 'Missing 2 issues: #1-2');
    assert.deepEqual(layout(page), ['#1 missing', '#2 missing', '/comics/Saga/Saga 003.cbz', '/comics/Saga/Saga 004.cbz']);
});

test('a complete series has no banner or placeholders', async () => {
    const page = await bootSeries({ issues: [issue(2), issue(1), issue(3)] });

    assert.equal(banner(page), null);
    assert.deepEqual(layout(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 003.cbz']);
});

test('a series without positive integer issue numbers has nothing missing', async () => {
    const page = await bootSeries({
        issues: [special('0', 'Saga 000'), issue('0.5'), special('Annual', 'Saga Annual')]
    });

    assert.equal(banner(page), null);
    assert.deepEqual(layout(page), ['/comics/Saga/Saga 000.cbz', '/comics/Saga/Saga 0.5.cbz', '/comics/Saga/Saga Annual.cbz']);
});

test('an issue number too large to represent is listed with the non-numeric issues', async () => {
    const huge = '9'.repeat(400);
    const page = await bootSeries({ issues: [special(huge, 'Saga Huge'), issue(2), issue(1)] });

    assert.deepEqual(layout(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga Huge.cbz']);
    assert.equal(banner(page), null, 'it does not count as the highest issue');
});
