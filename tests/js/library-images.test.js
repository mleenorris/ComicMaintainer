'use strict';

// Protected cover images in the series library (js/app/library.js): covers
// are fetched with the session token once they near the viewport, shown from
// blob URLs, cached (capped), retried once on transient failures, watched
// again after a growing pause while they keep failing, and replaced by the
// first issue's cover when an external image is missing.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    makeSeries,
    makeSeriesList,
    bootLibrary,
    callsTo,
    imageObserver,
    imageResponse
} = require('./support/library-helpers');

const COVER = '/api/comicreader/cover';
const coverCalls = page => callsTo(page, COVER);
const covers = page => page.$$('#fileList img[data-protected-image]');

/** Boot the library with `series` and a cover route answered by `cover`. */
function bootWithCovers(series, cover, extra = {}) {
    return bootLibrary({ series, ...extra, routes: { [`GET ${COVER}`]: cover, ...(extra.routes || {}) } });
}

/** Responses answered in order, the last one repeating. */
function sequence(...answers) {
    let index = 0;
    return call => {
        const answer = answers[Math.min(index++, answers.length - 1)];
        return typeof answer === 'function' ? answer(call) : answer;
    };
}

test('a cover is fetched with the session token once it nears the viewport and shown from a blob URL', async () => {
    const page = await bootWithCovers(makeSeriesList(2), imageResponse());
    const [first, second] = covers(page);
    const observer = imageObserver(page);
    assert.equal(coverCalls(page).length, 0);

    // Leaving the viewport margin does nothing.
    observer.trigger(false, [first]);
    await page.settle();
    assert.equal(coverCalls(page).length, 0);

    observer.trigger(true, [first]);
    await page.settle();

    const [call] = coverCalls(page);
    assert.deepEqual(call.query, { filePath: 'Series 001/Series 001 #001.cbz' });
    assert.match(call.headers.authorization, /^Bearer /);
    assert.match(first.src, /^blob:http:\/\/localhost\/test-\d+$/);
    assert.equal(page.objectUrls.get(first.src).type, 'image/jpeg');
    assert.equal(first.dataset.protectedImageLoaded, '1');
    // Loaded covers are no longer watched; the other one still is.
    assert.equal(observer.targets.has(first), false);
    assert.equal(observer.targets.has(second), true);
    assert.equal(second.getAttribute('src'), null);
});

test('covers already loaded are shown straight away when the library is re-rendered', async () => {
    const page = await bootWithCovers(makeSeriesList(2), imageResponse());
    imageObserver(page).trigger(true, [covers(page)[0]]);
    await page.settle();
    const loadedUrl = covers(page)[0].src;

    page.click('#seriesLayoutListBtn');

    const [first, second] = covers(page);
    assert.equal(first.className, 'series-list-thumb');
    assert.equal(first.src, loadedUrl);
    assert.equal(first.dataset.protectedImageLoaded, '1');
    assert.equal(imageObserver(page).targets.has(first), false);
    // The cover that never loaded waits for the viewport again.
    assert.equal(imageObserver(page).targets.has(second), true);
    assert.equal(coverCalls(page).length, 1);
});

test('series sharing a cover image fetch it only once', async () => {
    const shared = { has_external_image: true, external_image_url: '/api/series-images/shared' };
    const page = await bootLibrary({
        series: [makeSeries(1, shared), makeSeries(2, shared)],
        routes: { 'GET /api/series-images/shared': imageResponse() }
    });
    const [first, second] = covers(page);

    imageObserver(page).trigger(true, [first]);
    await page.settle();
    imageObserver(page).trigger(true, [second]);
    await page.settle();

    assert.equal(callsTo(page, '/api/series-images/shared').length, 1);
    assert.ok(first.src.startsWith('blob:'));
    assert.equal(second.src, first.src);
});

test('a missing external series image falls back to the first issue\'s cover', async () => {
    const series = makeSeries(1, { has_external_image: true, external_image_url: '/api/series-images/abc' });
    const page = await bootWithCovers([series], imageResponse(), {
        routes: { 'GET /api/series-images/abc': respond(404, { error: 'not found' }) }
    });
    const [cover] = covers(page);

    imageObserver(page).trigger(true, [cover]);
    await page.settle();

    assert.deepEqual(page.fetchCalls.filter(call => /series-images|comicreader/.test(call.path)).map(call => call.path),
        ['/api/series-images/abc', COVER]);
    assert.equal(coverCalls(page)[0].query.filePath, 'Series 001/Series 001 #001.cbz');
    assert.ok(cover.src.startsWith('blob:'));
});

test('a missing cover without a fallback is left empty and not requested again', async () => {
    const page = await bootWithCovers(makeSeriesList(1), respond(404, { error: 'not found' }));
    const [cover] = covers(page);

    imageObserver(page).trigger(true, [cover]);
    await page.settle();
    assert.equal(coverCalls(page).length, 1);
    assert.equal(cover.getAttribute('src'), null);

    // Hydrating the same card again does not re-request a known-missing cover.
    page.run('hydrateProtectedImages()');
    imageObserver(page).trigger(true, [cover]);
    await page.settle();
    assert.equal(coverCalls(page).length, 1);
});

test('an external image identical to the fallback is not requested twice', async () => {
    const series = makeSeries(1, { has_external_image: true, external_image_url: '/api/series-images/same' });
    series.cover_file_path = '/api/series-images/same';
    const page = await bootLibrary({ series: [series], routes: { 'GET /api/series-images/same': 404 } });

    imageObserver(page).trigger(true, covers(page));
    await page.settle();

    assert.equal(callsTo(page, '/api/series-images/same').length, 1);
    assert.equal(covers(page)[0].getAttribute('src'), null);
});

test('a series without any cover is never fetched', async () => {
    const page = await bootWithCovers([makeSeries(1, { cover_file_path: null })], imageResponse());
    const [cover] = covers(page);
    assert.equal(cover.dataset.protectedImage, '');

    imageObserver(page).trigger(true, [cover]);
    await page.settle();

    assert.equal(coverCalls(page).length, 0);
    assert.equal(cover.dataset.protectedImageLoaded, undefined);
});

test('a transient server error is retried once after a short pause', async t => {
    const page = await bootWithCovers(makeSeriesList(1), sequence(respond(503, 'busy'), imageResponse()));
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const [cover] = covers(page);

    imageObserver(page).trigger(true, [cover]);
    await page.settle();
    assert.equal(coverCalls(page).length, 1);
    assert.equal(cover.getAttribute('src'), null);

    t.mock.timers.tick(250);
    await page.settle();

    assert.equal(coverCalls(page).length, 2);
    assert.ok(cover.src.startsWith('blob:'));
});

test('a cover that keeps failing is given up for now and loads on a later attempt', async t => {
    const page = await bootWithCovers(makeSeriesList(1), sequence(500, 502, imageResponse()));
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const [cover] = covers(page);

    imageObserver(page).trigger(true, [cover]);
    await page.settle();
    t.mock.timers.tick(250);
    await page.settle();

    assert.equal(coverCalls(page).length, 2);
    assert.equal(cover.getAttribute('src'), null);
    assert.equal(cover.dataset.protectedImageLoaded, undefined);

    page.run('hydrateProtectedImages()');
    assert.ok(imageObserver(page).targets.has(cover));
    imageObserver(page).trigger(true, [cover]);
    await page.settle();

    assert.equal(coverCalls(page).length, 3);
    assert.ok(cover.src.startsWith('blob:'));
});

test('network failures are retried once, then logged', async t => {
    const page = await bootWithCovers(makeSeriesList(1), () => { throw new TypeError('Failed to fetch'); });
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const [cover] = covers(page);

    imageObserver(page).trigger(true, [cover]);
    await page.settle();
    t.mock.timers.tick(250);
    await page.settle();

    assert.equal(coverCalls(page).length, 2);
    const warnings = page.consoleMessages.filter(message => message.type === 'warn');
    assert.equal(warnings.length, 1);
    assert.equal(warnings[0].args[0], 'Protected image fetch failed');
    assert.match(String(warnings[0].args[1]), /Failed to fetch/);
    assert.equal(cover.dataset.protectedImageLoaded, undefined);
});

test('a refused cover (403) is not retried', async () => {
    const page = await bootWithCovers(makeSeriesList(1), 403);
    const [cover] = covers(page);

    imageObserver(page).trigger(true, [cover]);
    await page.settle();

    assert.equal(coverCalls(page).length, 1);
    assert.equal(cover.getAttribute('src'), null);
    assert.equal(cover.dataset.protectedImageLoaded, undefined);
});

test('an unreadable image body is logged and the cover left empty', async () => {
    const broken = () => new Response(new ReadableStream({
        start(controller) { controller.error(new Error('connection reset')); }
    }), { status: 200, headers: { 'Content-Type': 'image/jpeg' } });
    const page = await bootWithCovers(makeSeriesList(1), broken);
    const [cover] = covers(page);
    const objectUrlsBefore = page.objectUrls.size;

    imageObserver(page).trigger(true, [cover]);
    await page.settle();

    const errors = page.consoleMessages.filter(message => message.type === 'error' && message.args[0] === 'Failed to decode protected image');
    assert.equal(errors.length, 1);
    assert.equal(cover.getAttribute('src'), null);
    assert.equal(cover.dataset.protectedImageLoaded, undefined);
    assert.equal(page.objectUrls.size, objectUrlsBefore);
});

/** Show `cover` to the observer and let its request, and its quick retry, fail. */
async function failToLoad(t, page, cover) {
    imageObserver(page).trigger(true, [cover]);
    await page.settle();
    t.mock.timers.tick(250);
    await page.settle();
}

test('a cover that failed to load is watched again after a pause and fetched when scrolled back into view', async t => {
    const page = await bootWithCovers(makeSeriesList(1), sequence(503, 503, imageResponse()));
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const observer = imageObserver(page);
    const [cover] = covers(page);

    await failToLoad(t, page, cover);
    assert.equal(coverCalls(page).length, 2);
    assert.equal(cover.getAttribute('src'), null);
    // Not watched again at once, which would refetch it in a loop while it stays in view...
    assert.equal(observer.targets.has(cover), false);
    t.mock.timers.tick(1999);
    assert.equal(observer.targets.has(cover), false);
    // ...but after a pause.
    t.mock.timers.tick(1);
    assert.equal(observer.targets.has(cover), true);

    // Out of view nothing is fetched; back in view the cover loads.
    observer.trigger(false, [cover]);
    await page.settle();
    assert.equal(coverCalls(page).length, 2);
    observer.trigger(true, [cover]);
    await page.settle();

    assert.equal(coverCalls(page).length, 3);
    assert.ok(cover.src.startsWith('blob:'));
    assert.equal(observer.targets.has(cover), false);
});

test('a cover that keeps failing is watched again after pauses that double, five times at most', async t => {
    const page = await bootWithCovers(makeSeriesList(1), 503);
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const observer = imageObserver(page);
    const [cover] = covers(page);

    await failToLoad(t, page, cover);
    for (const pause of [2000, 4000, 8000, 16000, 32000]) {
        t.mock.timers.tick(pause - 1);
        assert.equal(observer.targets.has(cover), false, `before ${pause} ms`);
        t.mock.timers.tick(1);
        assert.equal(observer.targets.has(cover), true, `after ${pause} ms`);
        await failToLoad(t, page, cover);
    }
    assert.equal(coverCalls(page).length, 12);

    // Then it is left alone...
    t.mock.timers.tick(10 * 60 * 1000);
    assert.equal(observer.targets.has(cover), false);
    assert.equal(coverCalls(page).length, 12);
    assert.equal(cover.dataset.protectedImageLoaded, undefined);
    // ...until the library is rendered again.
    page.click('#seriesLayoutListBtn');
    assert.ok(observer.targets.has(covers(page)[0]));
});

test('a cover loaded before its retry is due is not watched again', async t => {
    const page = await bootWithCovers(makeSeriesList(1), sequence(503, 503, imageResponse()));
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const observer = imageObserver(page);
    const [cover] = covers(page);
    await failToLoad(t, page, cover);

    page.run('hydrateProtectedImages()');
    observer.trigger(true, [cover]);
    await page.settle();
    assert.ok(cover.src.startsWith('blob:'));
    t.mock.timers.tick(2000);

    assert.equal(observer.targets.has(cover), false);
    assert.equal(coverCalls(page).length, 3);
});

test('a cover that fails again while its retry is pending keeps that retry\'s pause', async t => {
    const page = await bootWithCovers(makeSeriesList(1), 503);
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const observer = imageObserver(page);
    const [cover] = covers(page);
    await failToLoad(t, page, cover);

    // Another hydration fails before the 2 s retry is due...
    page.run('hydrateProtectedImages()');
    await failToLoad(t, page, cover);
    assert.equal(coverCalls(page).length, 4);
    assert.equal(observer.targets.has(cover), false);

    // ...which neither adds a retry nor shortens or lengthens the pending one.
    t.mock.timers.tick(2000 - 250 - 1);
    assert.equal(observer.targets.has(cover), false);
    t.mock.timers.tick(1);
    assert.equal(observer.targets.has(cover), true);
    // The next pause is the second one (4 s), not the third.
    await failToLoad(t, page, cover);
    t.mock.timers.tick(4000 - 1);
    assert.equal(observer.targets.has(cover), false);
    t.mock.timers.tick(1);
    assert.equal(observer.targets.has(cover), true);
});

test('a cover replaced by a re-render before its retry is due is not watched again', async t => {
    const page = await bootWithCovers(makeSeriesList(1), 503);
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const observer = imageObserver(page);
    const [cover] = covers(page);
    await failToLoad(t, page, cover);

    page.click('#seriesLayoutListBtn');
    const [thumb] = covers(page);
    assert.notEqual(thumb, cover);
    t.mock.timers.tick(2000);

    assert.equal(observer.targets.has(cover), false);
    assert.equal(observer.targets.has(thumb), true);
});

test('a cover whose fallback failed to load is retried as well', async t => {
    const series = makeSeries(1, { has_external_image: true, external_image_url: '/api/series-images/abc' });
    const page = await bootWithCovers([series], sequence(503, 503, imageResponse()), {
        routes: { 'GET /api/series-images/abc': respond(404, { error: 'not found' }) }
    });
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const observer = imageObserver(page);
    const [cover] = covers(page);

    await failToLoad(t, page, cover);
    assert.equal(coverCalls(page).length, 2);
    assert.equal(cover.dataset.protectedImageLoaded, undefined);

    t.mock.timers.tick(2000);
    assert.equal(observer.targets.has(cover), true);
    observer.trigger(true, [cover]);
    await page.settle();

    assert.equal(callsTo(page, '/api/series-images/abc').length, 2);
    assert.equal(coverCalls(page).length, 3);
    assert.ok(cover.src.startsWith('blob:'));
});

test('the cover cache keeps at most 150 images, evicting and revoking the oldest', async () => {
    const page = await bootWithCovers(makeSeriesList(1), imageResponse());
    page.run(`for (let i = 0; i < MAX_PROTECTED_IMAGE_CACHE_ENTRIES; i++) {
        protectedImageUrls.set('old-' + i, URL.createObjectURL(new Blob(['x'])));
    }`);
    const oldest = page.evaluate("protectedImageUrls.get('old-0')");
    assert.ok(page.objectUrls.has(oldest));

    imageObserver(page).trigger(true, covers(page));
    await page.settle();

    assert.equal(page.evaluate('protectedImageUrls.size'), 150);
    assert.equal(page.evaluate("protectedImageUrls.has('old-0')"), false);
    assert.equal(page.evaluate("protectedImageUrls.has('old-1')"), true);
    assert.equal(page.objectUrls.has(oldest), false);
    assert.equal(page.evaluate("protectedImageUrls.get('Series 001/Series 001 #001.cbz')"), covers(page)[0].src);
});

test('without IntersectionObserver covers load eagerly, eight at a time', async () => {
    const pending = [];
    const page = await bootWithCovers(makeSeriesList(10), () => {
        const answer = deferred();
        pending.push(answer);
        return answer.promise;
    }, { globals: { IntersectionObserver: undefined } });

    assert.equal(imageObserver(page), undefined);
    assert.equal(covers(page).length, 10);
    assert.equal(coverCalls(page).length, 8);

    pending.forEach(answer => answer.resolve(imageResponse()));
    await page.settle();

    assert.equal(coverCalls(page).length, 10);
    pending.slice(8).forEach(answer => answer.resolve(imageResponse()));
    await page.settle();
    assert.ok(covers(page).every(cover => cover.src.startsWith('blob:')));
    assert.deepEqual(coverCalls(page).map(call => call.query.filePath), makeSeriesList(10).map(series => series.cover_file_path));
});

test('without IntersectionObserver a cover that failed to load waits for the next hydration', async t => {
    const answers = [];
    const page = await bootWithCovers(makeSeriesList(1), () => {
        const answer = deferred();
        answers.push(answer);
        return answer.promise;
    }, { globals: { IntersectionObserver: undefined } });
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const [cover] = covers(page);

    answers[0].resolve(503);
    await page.settle();
    t.mock.timers.tick(250);
    await page.settle();
    answers[1].resolve(503);
    await page.settle();
    assert.equal(cover.dataset.protectedImageLoaded, undefined);

    t.mock.timers.tick(10 * 60 * 1000);
    await page.settle();
    assert.equal(coverCalls(page).length, 2);
    assert.deepEqual(page.errors, []);

    page.run('hydrateProtectedImages()');
    await page.settle();
    assert.equal(coverCalls(page).length, 3);
    answers[2].resolve(imageResponse());
    await page.settle();
    assert.ok(cover.src.startsWith('blob:'));
});

test('cover tokens resolve to API paths or the cover endpoint', async () => {
    const page = await bootLibrary();

    assert.equal(page.evaluate("resolveProtectedImageUrl('/api/series-images/x')"), '/api/series-images/x');
    assert.equal(page.evaluate("resolveProtectedImageUrl('A & B/#1.cbz')"), '/api/comicreader/cover?filePath=A%20%26%20B%2F%231.cbz');
    assert.equal(page.evaluate("resolveProtectedImageUrl('')"), null);
    // The legacy page-extraction endpoint for a comic's first page.
    assert.equal(page.evaluate("getSeriesCoverFallbackUrl('A & B/#1.cbz')"), '/api/comicreader/page?filePath=A%20%26%20B%2F%231.cbz&page=1');
});
