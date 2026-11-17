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

test('a run of up to 12 missing issues gets a card per issue', async () => {
    const page = await bootSeries({ issues: [issue(1), issue(14)] });

    assert.equal(bannerText(page), 'Missing 12 issues: #2-13');
    assert.equal(page.$$('#seriesDetailPanel .series-issue-card--missing').length, 12);
});

test('a longer run of missing issues is one card for the whole range', async () => {
    const page = await bootSeries({ issues: [issue(1), issue('1.5'), issue(3), issue(40)] });

    assert.deepEqual(layout(page), [
        '/comics/Saga/Saga 001.cbz',
        '/comics/Saga/Saga 1.5.cbz',
        '#2 missing',
        '/comics/Saga/Saga 003.cbz',
        '#4-39 missing',
        '/comics/Saga/Saga 040.cbz'
    ]);
    const run = page.$$('#seriesDetailPanel .series-issue-card--missing')[1];
    assert.equal(run.getAttribute('aria-label'), 'Missing issues #4-39');
    assert.equal(run.getAttribute('title'), 'Missing issues #4-39');
    assert.equal(text(run.querySelector('.series-issue-subtitle')), '36 issues, #4 to #39, are not in your collection');
    assert.equal(bannerText(page), 'Missing 37 issues: #2, #4-39');
});

test('an issue numbered like a year is one card however far it is from the rest', async () => {
    const page = await bootSeries({ issues: [issue(1), issue(2), issue(2024)] });

    assert.deepEqual(layout(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '#3-2023 missing', '/comics/Saga/Saga 2024.cbz']);
    assert.equal(bannerText(page), 'Missing 2021 issues: #3-2023');
});

test('issue numbers above 100000, such as dates, leave no gap before them', async () => {
    const page = await bootSeries({ issues: [issue(20240115), issue(2), issue(1)] });

    assert.deepEqual(layout(page), ['/comics/Saga/Saga 001.cbz', '/comics/Saga/Saga 002.cbz', '/comics/Saga/Saga 20240115.cbz']);
    assert.equal(banner(page), null);
});

// An issue of one volume of Green Lantern: volume_key/volume_label as the server sends them.
const volumeIssue = (number, year) => issue(number, {
    file_path: `/comics/Green Lantern/Green Lantern (${year}) - Chapter ${String(number).padStart(4, '0')}.cbz`,
    file_name: `Green Lantern (${year}) - Chapter ${String(number).padStart(4, '0')}.cbz`,
    volume_key: year ? `y${year}` : null,
    volume_label: year ? String(year) : null
});
const volumeLayout = page => page.$$('#seriesDetailPanel .series-issues-grid > *').map(element => {
    if (element.matches('.series-issues-volume-heading')) return `== ${text(element)}`;
    if (element.classList.contains('series-issue-card--missing')) return `${element.querySelector('.series-issue-badge').textContent} missing`;
    return `${element.dataset.volume || ''}#${text(element.querySelector('.series-issue-badge')).replace('#', '')}`;
});

test('a series whose numbering restarts with each volume shows each volume with its own missing issues', async () => {
    // As the server orders them: 1990 #1, #3, then 2005 #1, #2, then an issue of no known volume.
    const page = await bootSeries({
        issues: [volumeIssue(1, 1990), volumeIssue(3, 1990), volumeIssue(1, 2005), volumeIssue(2, 2005), volumeIssue(4, null)]
    });

    assert.deepEqual(volumeLayout(page), [
        '== 1990 2 issues', '#1', '#2 missing', '#3',
        '== 2005 2 issues', '#1', '#2',
        '== No volume 1 issue', '#1 missing', '#2 missing', '#3 missing', '#4'
    ]);
    assert.equal(bannerText(page), 'Missing 4 issues: 1990: #2; No volume: #1-3');
});

test('volumes that continue one numbering, like a manga\'s, stay one run', async () => {
    const page = await bootSeries({ issues: [volumeIssue(1, 1990), volumeIssue(2, 2005), volumeIssue(4, 2005)] });

    assert.equal(page.$('#seriesDetailPanel .series-issues-volume-heading'), null);
    assert.equal(bannerText(page), 'Missing 1 issue: #3');
});

test('one known volume is one run', async () => {
    const page = await bootSeries({ issues: [volumeIssue(1, 1990), volumeIssue(1, null), volumeIssue('Annual', 1990)] });

    assert.equal(page.$('#seriesDetailPanel .series-issues-volume-heading'), null);
});

test('Annuals get a section of their own, with their own missing issues', async () => {
    const annual = number => issue(number, {
        file_path: `/comics/Saga/Saga Annual - Chapter ${number}.cbz`,
        file_name: `Saga Annual - Chapter ${number}.cbz`,
        annual: true
    });
    // As the server orders them: the series' issues, then the Annuals.
    const page = await bootSeries({ issues: [issue(1), issue(3), annual(1), annual(3)] });

    assert.deepEqual(volumeLayout(page), [
        '== Issues 2 issues', '#1', '#2 missing', '#3',
        '== Annual 2 issues', '#1', '#2 missing', '#3'
    ]);
    assert.equal(bannerText(page), 'Missing 2 issues: Issues: #2; Annual: #2');
});

test('Specials get a section of their own after the Annuals, and other kinds after them', async () => {
    const kindOf = (number, format) => issue(number, {
        file_path: `/comics/Saga/Saga ${format} - Chapter ${number}.cbz`,
        file_name: `Saga ${format} - Chapter ${number}.cbz`,
        format,
        annual: format === 'Annual'
    });
    // Sent in another order than they are listed.
    const page = await bootSeries({
        issues: [kindOf(1, 'Preview'), kindOf(1, 'Special'), kindOf(3, 'Special'), issue(1), kindOf(1, 'Annual'), kindOf(1, 'Bonus')]
    });

    assert.deepEqual(volumeLayout(page), [
        '== Issues 1 issue', '#1',
        '== Annual 1 issue', '#1',
        '== Special 2 issues', '#1', '#2 missing', '#3',
        '== Bonus 1 issue', '#1',
        '== Preview 1 issue', '#1'
    ]);
    assert.equal(bannerText(page), 'Missing 1 issue: Special: #2');
});

test('the Specials of a volume series are named after their volume', async () => {
    const specialOf = (number, year) => ({ ...volumeIssue(number, year), file_path: `/comics/Green Lantern/S${year} ${number}.cbz`, format: 'Special' });
    const page = await bootSeries({
        issues: [specialOf(1, 1990), specialOf(1, 2005), { ...specialOf(2, null), file_path: '/comics/Green Lantern/S2.cbz' }]
    });

    assert.deepEqual(page.$$('#seriesDetailPanel .series-issues-volume-heading').map(text),
        ['1990 Special 1 issue', '2005 Special 1 issue', 'Special 1 issue']);
});

test('the Annuals of a volume series are named after their volume', async () => {
    const annualOf = (number, year) => ({ ...volumeIssue(number, year), file_path: `/comics/Green Lantern/Annual ${year} ${number}.cbz`, annual: true });
    const page = await bootSeries({
        issues: [volumeIssue(1, 1990), volumeIssue(1, 2005), annualOf(1, 1990), annualOf(1, 2005)]
    });

    assert.deepEqual(page.$$('#seriesDetailPanel .series-issues-volume-heading').map(text),
        ['1990 1 issue', '2005 1 issue', '1990 Annual 1 issue', '2005 Annual 1 issue']);
});

test('only Annuals of one volume make one section named after it', async () => {
    const annual = number => ({ ...volumeIssue(number, 1990), file_path: `/comics/Green Lantern/A${number}.cbz`, annual: true });
    const page = await bootSeries({ issues: [annual(1), annual(2)] });

    assert.deepEqual(page.$$('#seriesDetailPanel .series-issues-volume-heading').map(text), ['1990 Annual 2 issues']);
});

test('the series issues of mixed volumes, beside Annuals, are named "Issues"', async () => {
    // 1990 #1 and 2005 #2 continue one numbering, so they stay one run.
    const page = await bootSeries({
        issues: [volumeIssue(1, 1990), volumeIssue(2, 2005), { ...volumeIssue(1, null), file_path: '/comics/Green Lantern/A1.cbz', annual: true }]
    });

    assert.deepEqual(page.$$('#seriesDetailPanel .series-issues-volume-heading').map(text), ['Issues 2 issues', 'Annual 1 issue']);
});
