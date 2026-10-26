'use strict';

// The Series Image section of the Manage Series Names modal
// (js/app/series-metadata.js): the protected cover preview, uploading and
// clearing a custom image, and fetching a cover from an external provider.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootSeries, metadataRecord, messages, lastMessage, callsTo, text, libraryRefreshes, openManageNames, buttonIn
} = require('./support/series-helpers');

const MODAL = '#manageSeriesNamesModal';
const IMAGE = '/api/series-images/saga';
const UPLOAD = '/api/series-images/Saga';
const CANDIDATES = '/api/series-images/candidates';
const FROM_PROVIDER = '/api/series-images/Saga/from-provider';
const NO_IMAGE = 'No image cached. Refresh metadata or upload one below.';
const PANEL_HINT = 'Enter a title and click Search to find cover candidates from the configured providers.';
const png = respond(200, 'PNGDATA', { 'Content-Type': 'image/png' });
const offline = () => { throw new TypeError('Failed to fetch'); };

const bearer = page => `Bearer ${page.window.localStorage.getItem('jwt_token')}`;
const preview = page => page.$('#manageSeriesImagePreview');
const imageStatus = page => text(page.$('#manageSeriesImageStatus'));
const panel = page => page.$('#manageSeriesImageProviderPanel');
const providerQuery = page => page.$('#manageSeriesImageProviderQuery');
const providerStatus = page => text(page.$('#manageSeriesImageProviderStatus'));
const candidateCards = page => [...page.$('#manageSeriesImageProviderResults').children];
const localTime = (page, iso) => page.run(`new Date(${JSON.stringify(iso)}).toLocaleString()`);
const logged = (page, label) => page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === label);
const imageButton = (page, label) => buttonIn(page, MODAL, label);

// The blob behind the preview, when one is shown.
const previewBlob = page => (preview(page).hasAttribute('src') ? page.objectUrls.get(preview(page).src) : null);

// Puts `file` (or nothing) in the image input as the file dialog would and
// fires its change event. Setting value = '' empties the input again, as in a
// browser.
function chooseFile(page, file) {
    const input = page.$('#manageSeriesImageInput');
    let files = file ? [file] : [];
    Object.defineProperty(input, 'files', { configurable: true, get: () => files });
    Object.defineProperty(input, 'value', {
        configurable: true,
        get: () => (files.length ? `C:\\fakepath\\${files[0].name}` : ''),
        set: value => {
            if (value === '') files = [];
        }
    });
    input.dispatchEvent(new page.window.Event('change'));
    return input;
}

const imageFile = (page, name, type) => new page.window.File(['IMAGEDATA'], name, { type });

async function searchCovers(page) {
    page.click(`${MODAL} button[onclick="searchSeriesImageCandidates()"]`);
    await page.settle();
}

// GET /api/series-images/candidates (SeriesImageCandidate, snake_case).
const COVERS = [
    {
        image_url: 'https://comicvine.example/saga-full.jpg',
        thumbnail_url: 'https://comicvine.example/saga-thumb.jpg',
        source: 'ComicVine',
        canonical_title: 'Saga'
    },
    { image_url: 'https://mangadex.example/saga.png', thumbnail_url: null, source: null, canonical_title: null }
];

async function coverSearchPage(record = metadataRecord()) {
    const page = await bootSeries({ routes: { [`GET ${IMAGE}`]: png } });
    await openManageNames(page, record);
    page.click(imageButton(page, 'Fetch from Provider'));
    page.route(`GET ${CANDIDATES}`, { candidates: COVERS });
    await searchCovers(page);
    return page;
}

test('the cached series image is previewed with where it came from', async () => {
    const page = await bootSeries({ routes: { [`GET ${IMAGE}`]: png } });

    await openManageNames(page, metadataRecord({
        has_image: true,
        is_user_image: true,
        image_downloaded_utc: '2024-02-01T12:00:00Z'
    }));

    assert.equal(imageStatus(page), `User-uploaded · ${localTime(page, '2024-02-01T12:00:00Z')}`);
    const [get] = callsTo(page, IMAGE, 'GET');
    assert.equal(get.headers.authorization, bearer(page), 'the image is fetched with the session token');
    assert.equal(previewBlob(page).type, 'image/png');

    page.click(`${MODAL} .close-btn`);
    await openManageNames(page, metadataRecord({ has_image: true }));
    assert.equal(imageStatus(page), 'Downloaded from provider · unknown');
    assert.equal(callsTo(page, IMAGE, 'GET').length, 2);

    page.click(`${MODAL} .close-btn`);
    await openManageNames(page, metadataRecord({ has_image: true, series_id: null }));
    assert.equal(imageStatus(page), NO_IMAGE, 'without a series id there is no image to show');
    assert.equal(preview(page).hasAttribute('src'), false, 'the previous series\' cover is not left behind');
    assert.equal(callsTo(page, IMAGE, 'GET').length, 2);
});

test('a cover that cannot be fetched leaves the preview empty', async () => {
    const page = await bootSeries({ routes: { [`GET ${IMAGE}`]: 404 } });

    await openManageNames(page, metadataRecord({ has_image: true }));
    assert.equal(callsTo(page, IMAGE, 'GET').length, 1);
    assert.equal(preview(page).hasAttribute('src'), false);

    page.route(`GET ${IMAGE}`, offline);
    page.click(`${MODAL} .close-btn`);
    await openManageNames(page, metadataRecord({ has_image: true }));
    assert.equal(preview(page).hasAttribute('src'), false);
    assert.ok(logged(page, 'fetchProtectedImageInto failed'));
    assert.equal(imageStatus(page), 'Downloaded from provider · unknown');
});

test('Upload Image opens the file picker and a chosen image becomes the series cover', async () => {
    const page = await bootSeries({ routes: { [`GET ${IMAGE}`]: png } });
    await openManageNames(page);
    const input = page.$('#manageSeriesImageInput');
    let pickerOpened = 0;
    input.click = () => { pickerOpened++; };

    page.click(imageButton(page, 'Upload Image'));
    assert.equal(pickerOpened, 1);

    page.route(`PUT ${UPLOAD}`, metadataRecord({
        has_image: true,
        is_user_image: true,
        image_downloaded_utc: '2024-07-01T09:30:00Z'
    }));
    const refreshes = libraryRefreshes(page);
    chooseFile(page, imageFile(page, 'cover.webp', 'image/webp'));
    await page.settle();

    const [put] = callsTo(page, UPLOAD, 'PUT');
    const sent = put.body.get('file');
    assert.equal(sent.name, 'cover.webp');
    assert.equal(sent.type, 'image/webp');
    assert.equal(put.headers.authorization, bearer(page));
    assert.deepEqual(lastMessage(page), { text: 'Series image updated', type: 'success' });
    assert.equal(imageStatus(page), `User-uploaded · ${localTime(page, '2024-07-01T09:30:00Z')}`);
    assert.equal(previewBlob(page).type, 'image/png');
    assert.equal(libraryRefreshes(page), refreshes + 1, 'library cards pick up the new cover');
    assert.equal(input.files.length, 0, 'the input is reset so the same file can be picked again');
});

test('only JPEG, PNG and WEBP images are uploaded', async () => {
    const page = await bootSeries();
    await openManageNames(page);

    const input = chooseFile(page, imageFile(page, 'cover.gif', 'image/gif'));
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Only JPEG, PNG, or WEBP images are supported', type: 'error' });
    assert.equal(input.files.length, 0);

    chooseFile(page, null);
    await page.settle();
    assert.equal(messages(page).length, 1, 'cancelling the file dialog does nothing');
    assert.equal(callsTo(page, UPLOAD).length, 0);
});

test('a failed upload is reported and the input is reset', async () => {
    const page = await bootSeries();
    await openManageNames(page);
    const refreshes = libraryRefreshes(page);
    const outcomes = [
        [respond(400, { error: 'Image exceeds the 10 MB limit' }), 'Image exceeds the 10 MB limit'],
        [respond(400, {}), 'Failed to upload series image'],
        [respond(415, 'Unsupported Media Type'), 'Failed to upload series image'],
        [offline, 'Failed to upload series image']
    ];

    for (const [handler, expected] of outcomes) {
        page.route(`PUT ${UPLOAD}`, handler);
        const input = chooseFile(page, imageFile(page, 'cover.png', 'image/png'));
        await page.settle();
        assert.deepEqual(lastMessage(page), { text: expected, type: 'error' });
        assert.equal(input.files.length, 0);
    }
    assert.equal(callsTo(page, UPLOAD, 'PUT').length, outcomes.length);
    assert.ok(logged(page, 'uploadManageSeriesImage failed'));
    assert.equal(libraryRefreshes(page), refreshes);
    assert.equal(imageStatus(page), NO_IMAGE);
});

test('Clear Image removes the cached image', async () => {
    const page = await bootSeries({ routes: { [`GET ${IMAGE}`]: png } });
    await openManageNames(page, metadataRecord({ has_image: true, is_user_image: true }));
    assert.ok(previewBlob(page));
    page.route(`DELETE ${IMAGE}`, metadataRecord());
    const refreshes = libraryRefreshes(page);

    page.click(imageButton(page, 'Clear Image'));
    await page.settle();

    const [del] = callsTo(page, IMAGE, 'DELETE');
    assert.equal(del.headers.authorization, bearer(page));
    assert.deepEqual(lastMessage(page), { text: 'Series image cleared', type: 'success' });
    assert.equal(imageStatus(page), NO_IMAGE);
    assert.equal(preview(page).hasAttribute('src'), false);
    assert.equal(libraryRefreshes(page), refreshes + 1);

    page.route(`DELETE ${IMAGE}`, 404);
    page.click(imageButton(page, 'Clear Image'));
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Series image cleared', type: 'success' }, 'already gone is fine');
    assert.equal(libraryRefreshes(page), refreshes + 2);

    for (const handler of [respond(500, 'Error deleting image'), offline]) {
        page.route(`DELETE ${IMAGE}`, handler);
        page.click(imageButton(page, 'Clear Image'));
        await page.settle();
        assert.deepEqual(lastMessage(page), { text: 'Failed to clear series image', type: 'error' });
    }
    assert.ok(logged(page, 'clearManageSeriesImage failed'));
    assert.equal(libraryRefreshes(page), refreshes + 2);
});

test('Clear Image without a loaded series record has nothing to clear', async () => {
    const page = await bootSeries();
    await openManageNames(page, respond(500));

    page.click(imageButton(page, 'Clear Image'));
    await page.settle();

    assert.deepEqual(lastMessage(page), { text: 'No image to clear', type: 'info' });
    assert.equal(page.fetchCalls.filter(call => call.method === 'DELETE').length, 0);
});

test('Fetch from Provider opens a cover search prefilled with the series title', async () => {
    const page = await bootSeries();
    await openManageNames(page);
    const query = providerQuery(page);

    page.click(imageButton(page, 'Fetch from Provider'));

    assert.equal(panel(page).style.display, 'block');
    assert.equal(query.value, 'Saga');
    assert.equal(page.document.activeElement, query);
    assert.equal(providerStatus(page), PANEL_HINT);

    page.input(query, '   ');
    await searchCovers(page);
    assert.equal(providerStatus(page), 'Enter a title to search.');
    page.input(query, '');
    page.$('#manageSeriesImageProviderStatus').textContent = '';
    await searchCovers(page);
    assert.equal(providerStatus(page), 'Enter a title to search.');
    assert.equal(callsTo(page, CANDIDATES).length, 0);

    const pending = deferred();
    page.route(`GET ${CANDIDATES}`, () => pending.promise);
    page.input(query, ' Saga Deluxe ');
    const typing = new page.window.KeyboardEvent('keydown', { key: 'a', cancelable: true });
    query.dispatchEvent(typing);
    assert.equal(callsTo(page, CANDIDATES).length, 0, 'only Enter searches');
    const enter = new page.window.KeyboardEvent('keydown', { key: 'Enter', cancelable: true });
    query.dispatchEvent(enter);
    await page.settle();

    assert.ok(enter.defaultPrevented);
    assert.equal(providerStatus(page), 'Searching providers...');
    const [get] = callsTo(page, CANDIDATES, 'GET');
    assert.deepEqual({ ...get.query }, { query: 'Saga Deluxe', limit: '12' });
    assert.equal(get.headers.authorization, bearer(page));

    pending.resolve({ candidates: COVERS });
    await page.settle();
    assert.equal(providerStatus(page), '2 candidates — click a thumbnail to apply.');
    assert.deepEqual(candidateCards(page).map(card => {
        const img = card.querySelector('img');
        return {
            src: img.src,
            alt: img.alt,
            referrerPolicy: img.referrerPolicy,
            loading: img.loading,
            labels: [...card.children].slice(1).map(text)
        };
    }), [
        {
            src: 'https://comicvine.example/saga-thumb.jpg',
            alt: 'Saga',
            referrerPolicy: 'no-referrer',
            loading: 'lazy',
            labels: ['Saga', 'ComicVine']
        },
        {
            src: 'https://mangadex.example/saga.png',
            alt: 'Series cover',
            referrerPolicy: 'no-referrer',
            loading: 'lazy',
            labels: ['', '']
        }
    ]);
    assert.equal(candidateCards(page)[0].title, 'Saga (ComicVine)');

    page.click(`${MODAL} button[onclick="closeFetchSeriesImageFromProvider()"]`);
    assert.equal(panel(page).style.display, 'none');

    page.click(imageButton(page, 'Fetch from Provider'));
    assert.equal(candidateCards(page).length, 0, 'reopening starts a fresh search');
    assert.equal(providerStatus(page), PANEL_HINT);
});

test('failed and empty cover searches say so', async () => {
    const page = await coverSearchPage();
    assert.equal(candidateCards(page).length, 2);
    const outcomes = [
        [respond(502, { error: 'Bad gateway' }), 'Search failed.'],
        [offline, 'Search failed.'],
        [{ candidates: [] }, 'No provider returned a cover image for that query.'],
        [null, 'No provider returned a cover image for that query.'],
        [{ candidates: [COVERS[0]] }, '1 candidate — click a thumbnail to apply.']
    ];

    for (const [handler, expected] of outcomes) {
        page.route(`GET ${CANDIDATES}`, handler);
        await searchCovers(page);
        assert.equal(providerStatus(page), expected);
    }
    assert.equal(candidateCards(page).length, 1);
    assert.ok(logged(page, 'searchSeriesImageCandidates failed'));
});

test('clicking a cover candidate downloads it as the series image', async () => {
    const page = await coverSearchPage();
    const pending = deferred();
    page.route(`POST ${FROM_PROVIDER}`, () => pending.promise);
    const refreshes = libraryRefreshes(page);

    page.click(candidateCards(page)[0]);
    await page.settle();

    assert.equal(providerStatus(page), 'Downloading selected image...');
    const [post] = callsTo(page, FROM_PROVIDER, 'POST');
    assert.deepEqual(post.body, { imageUrl: 'https://comicvine.example/saga-full.jpg', source: 'ComicVine' });
    assert.equal(post.headers['content-type'], 'application/json');
    assert.equal(post.headers.authorization, bearer(page));

    pending.resolve(metadataRecord({ has_image: true, image_downloaded_utc: '2024-08-15T18:45:00Z' }));
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Series image updated from provider', type: 'success' });
    assert.equal(imageStatus(page), `Downloaded from provider · ${localTime(page, '2024-08-15T18:45:00Z')}`);
    assert.equal(previewBlob(page).type, 'image/png');
    assert.equal(panel(page).style.display, 'none');
    assert.equal(libraryRefreshes(page), refreshes + 1);
});

test('a cover candidate that cannot be downloaded is reported in the panel', async () => {
    const page = await coverSearchPage();
    const refreshes = libraryRefreshes(page);
    const outcomes = [
        [respond(400, { error: 'Image URL is not from a configured provider' }), 'Image URL is not from a configured provider'],
        [respond(502, 'Bad Gateway'), 'Failed to apply provider image'],
        [offline, 'Failed to apply provider image']
    ];

    for (const [handler, expected] of outcomes) {
        page.route(`POST ${FROM_PROVIDER}`, handler);
        page.click(candidateCards(page)[1]);
        await page.settle();
        assert.equal(providerStatus(page), expected);
        assert.deepEqual(lastMessage(page), { text: expected, type: 'error' });
    }
    assert.deepEqual(callsTo(page, FROM_PROVIDER, 'POST')[0].body, { imageUrl: 'https://mangadex.example/saga.png', source: null });
    assert.ok(logged(page, 'applySeriesImageCandidate failed'));
    assert.equal(panel(page).style.display, 'block');
    assert.equal(libraryRefreshes(page), refreshes);
});

test('cover candidates without a full image, or left over in a closed modal, are not applied', async () => {
    const page = await bootSeries();
    await openManageNames(page);
    page.click(imageButton(page, 'Fetch from Provider'));
    page.route(`GET ${CANDIDATES}`, { candidates: [{ ...COVERS[0], image_url: null }, COVERS[1]] });
    await searchCovers(page);

    page.click(candidateCards(page)[0]);
    await page.settle();
    assert.equal(providerStatus(page), '2 candidates — click a thumbnail to apply.');

    page.click(`${MODAL} .close-btn`);
    page.click(candidateCards(page)[1]);
    await page.settle();
    assert.equal(callsTo(page, FROM_PROVIDER).length, 0);

    page.click(imageButton(page, 'Fetch from Provider'));
    assert.equal(providerQuery(page).value, '', 'no series is being managed');
});
