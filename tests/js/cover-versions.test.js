'use strict';

// Cover addresses carry the file's version (its size and modified time): the
// browser, the service worker and the page keep a cover by its address, and a
// rename can give a path to another file (Green Lantern: New Guardians'
// Chapter 0005 held issue 4, then issue 5), whose cover must not be the
// previous file's.

const test = require('node:test');
const assert = require('node:assert/strict');
const { seriesCard, issue, bootSeries, issueCards } = require('./support/series-helpers');

const coverOf = (path, version) => `/api/comicreader/cover?filePath=${encodeURIComponent(path)}${version ? `&v=${encodeURIComponent(version)}` : ''}`;

test("an issue's cover address carries its size and modified time", async () => {
    const page = await bootSeries({ issues: [issue(1, { size: 4096, modified: 1795193984 }), issue(2, { modified: undefined })] });

    const tokens = issueCards(page).map(card => card.querySelector('img.series-issue-cover').dataset.protectedImage);

    assert.deepEqual(tokens, [
        coverOf('/comics/Saga/Saga 001.cbz', '4096-1795193984'),
        coverOf('/comics/Saga/Saga 002.cbz')
    ], 'without a modified time there is no version to give');
});

test("a series' cover from its first issue carries the version the list gives", async () => {
    const page = await bootSeries({ series: seriesCard({ cover_version: '9-8' }) });

    assert.equal(page.$('#seriesDetailPanel .series-detail-cover').dataset.protectedImage, coverOf('/comics/Saga/Saga 001.cbz', '9-8'));
});

test('a cover token names its file whatever its version', async () => {
    const page = await bootSeries();

    assert.equal(page.evaluate(`coverTokenFilePath(${JSON.stringify(coverOf('/c/A 1.cbz', '1-2'))})`), '/c/A 1.cbz');
    assert.equal(page.evaluate("coverTokenFilePath('/c/A 1.cbz')"), '/c/A 1.cbz', 'a bare path, as older tokens were');
    assert.equal(page.evaluate("coverTokenFilePath('/api/series-images/saga?w=480')"), null);
    assert.equal(page.evaluate("coverTokenFilePath('')"), null);
    assert.equal(page.evaluate('coverTokenFilePath(null)'), null);
});

test('a replaced cover is fetched again and redrawn at every versioned address', async () => {
    const file = '/comics/Saga/Saga 001.cbz';
    const versioned = coverOf(file, '1048576-1700000000');
    const page = await bootSeries({ routes: { 'GET /api/comicreader/cover': () => new Response('cover', { headers: { 'Content-Type': 'image/webp' } }) } });
    await page.settle();
    const image = issueCards(page)[0].querySelector('img.series-issue-cover');
    assert.equal(image.dataset.protectedImage, versioned);
    page.run(`protectedImageUrls.set(${JSON.stringify(versioned)}, 'blob:old-cover')`);
    image.src = 'blob:old-cover';
    image.dataset.protectedImageLoaded = '1';

    await page.evaluate(`refreshFileCover(${JSON.stringify(file)})`);
    await page.settle();

    // The series' header shows the same file's cover, at the address its list entry gave.
    const reloads = page.fetchCalls.filter(call => call.init && call.init.cache === 'reload');
    assert.deepEqual(reloads.map(call => call.url).sort(), [coverOf(file), versioned]);
    assert.notEqual(page.evaluate(`protectedImageUrls.get(${JSON.stringify(versioned)})`), 'blob:old-cover');
    assert.notEqual(image.getAttribute('src'), 'blob:old-cover');
});
