'use strict';

// Finding a series on Suwayomi (js/app/suwayomi-search.js): the entry points
// that appear once the server answers as connected (sidebar, menu and the
// library's "No series found" state), the search across the Suwayomi library
// and every installed source of the picked language, and adding a series,
// which puts it in the Suwayomi library and queues the issues the local
// library lacks.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const { boot, consoleLines, messages, press, requests, seriesCard, seriesPage, wentToLogin } = require('./support/core-helpers');
const { imageObserver } = require('./support/library-helpers');

const CONNECTED = { enabled: true, configured: true, connected: true, serverName: 'Suwayomi', version: 'v1.1.1', error: null };
const ENTRY_POINTS = ['#sidebarSuwayomiSearchItem', '#suwayomiSearchMenuItem'];

// One source of GET /api/suwayomi/sources (SuwayomiSource): ids are decimal strings.
const source = (id, name, lang) => ({ id, name, lang, displayName: `${name} (${lang.toUpperCase()})` });
// As the server lists them: by display name, the local source left out.
const SOURCES = [source('101', 'Comick', 'all'), source('102', 'MangaDex', 'en'), source('103', 'Rawkuma', 'ja')];

// One series of a search answer (SuwayomiSeries).
const series = (id, title, overrides = {}) => ({ id, title, inLibrary: false, source: null, thumbnailUrl: null, ...overrides });

// POST /api/suwayomi/series/{id}/add (SuwayomiAddSeriesResult).
function addResult({ id = 2, title = 'Saga', alreadyInLibrary = false, localSeriesTitle = null, chapterCount = 0, queued = 0, owned = 0, downloaded = 0, unnumbered = 0 } = {}) {
    return {
        series: series(id, title, { inLibrary: true }),
        alreadyInLibrary,
        localSeriesTitle,
        downloads: {
            chapterCount,
            queuedChapters: Array.from({ length: queued }, (_, index) => ({ id: 500 + index, name: `Chapter ${index + 1}`, chapterNumber: index + 1, isDownloaded: false })),
            alreadyInLocalLibraryCount: owned,
            alreadyDownloadedCount: downloaded,
            duplicateChapterCount: 0,
            unnumberedChapterCount: unnumbered
        }
    };
}

// Signs in onto the library with a connected Suwayomi server that has
// `sources`; its library search answers `library` and each source's search
// answers found[sourceId]. `languages` is the browser's navigator.languages.
async function bootSearch({ sources = SOURCES, library = [], found = {}, languages, routes = {}, ...options } = {}) {
    const sourceRoutes = Object.fromEntries(sources
        .filter(item => item && typeof item.id === 'string')
        .map(item => [`GET /api/suwayomi/sources/${item.id}/series`, { series: found[item.id] || [], hasNextPage: false }]));
    return boot({
        hash: '#/library',
        ...options,
        routes: {
            'GET /api/suwayomi/status': CONNECTED,
            'GET /api/suwayomi/sources': { sources },
            'GET /api/suwayomi/series': { series: library },
            ...sourceRoutes,
            ...routes
        },
        beforeScripts(window) {
            if (languages) Object.defineProperty(window.navigator, 'languages', { value: languages, configurable: true });
        }
    });
}

const text = element => (element ? element.textContent.replace(/\s+/g, ' ').trim() : null);
const dialog = page => page.$('#suwayomiSearchModal');
const isOpen = page => dialog(page).classList.contains('active');
const status = page => page.$('#suwayomiSearchStatus').textContent;
const results = page => page.$('#suwayomiSearchResults');
const languageSelect = page => page.$('#suwayomiSearchLanguage');
const optionTexts = select => [...select.options].map(option => option.textContent);
const item = (page, id) => page.$(`#suwayomiSearchResults [data-suwayomi-series-id="${id}"]`);
const addButton = (page, id) => item(page, id).querySelector('button');
const failures = page => page.$('#suwayomiSearchResults .suwayomi-search-failures');
// What adding a series reported in the dialog; messages(page) is what reached the page.
const dialogMessages = page => messages(page, '#suwayomiSearchMessages');

// The visible result sections, each with the titles it lists.
const sections = page => page.$$('#suwayomiSearchResults .suwayomi-search-section')
    .filter(section => !section.hidden)
    .map(section => ({
        title: text(section.querySelector('.suwayomi-search-section-title')),
        series: [...section.querySelectorAll('.suwayomi-search-title')].map(text)
    }));

// GET /api/suwayomi/sources/{id}/series requests, in order.
const sourceSearches = page => page.fetchCalls.filter(call => /^\/api\/suwayomi\/sources\/\d+\/series$/.test(call.path));
const searchedSourceIds = page => sourceSearches(page).map(call => call.path.split('/')[4]);
const librarySearches = page => requests(page, 'GET', '/api/suwayomi/series');

async function openFromSidebar(page) {
    page.click('#sidebarSuwayomiSearchItem');
    await page.settle();
}

// Types `query` into the dialog (opening it from the sidebar if need be) and submits it.
async function search(page, query) {
    if (!isOpen(page)) await openFromSidebar(page);
    page.input('#suwayomiSearchInput', query);
    page.click('#suwayomiSearchButton');
    await page.settle();
}

// Searches the library from the header search box, as typing there does once it pauses.
async function librarySearch(page, term) {
    page.run(`document.getElementById('headerSearchInput').value = ${JSON.stringify(term)}; filterFiles()`);
    await page.settle();
}

const libraryWithout = page => call => seriesPage(call.query.search ? [] : [seriesCard()]);

test('"Find on Suwayomi" is offered in the sidebar and the menu only once the server answers as connected', async () => {
    const disabled = await boot({ hash: '#/library' });
    for (const selector of ENTRY_POINTS) {
        assert.equal(disabled.$(selector).hidden, true, selector);
        assert.ok(disabled.$(selector).hasAttribute('data-requires-write'), `${selector} is hidden from read-only users`);
    }
    assert.equal(requests(disabled, 'GET', '/api/suwayomi/status').length, 1, 'the boot probes the server once');

    for (const answer of [{ ...CONNECTED, connected: false, error: 'Connection refused' }, respond(500, { error: 'boom' })]) {
        const page = await bootSearch({ routes: { 'GET /api/suwayomi/status': answer } });
        for (const selector of ENTRY_POINTS) assert.equal(page.$(selector).hidden, true, selector);
    }

    const page = await bootSearch();
    for (const selector of ENTRY_POINTS) assert.equal(page.$(selector).hidden, false, selector);
    assert.equal(requests(page, 'GET', '/api/suwayomi/status').length, 1);

    page.click('#settingsMenuToggle');
    page.click('#suwayomiSearchMenuItem');
    await page.settle();
    assert.ok(isOpen(page));
    assert.equal(page.$('#settingsDropdownMenu').classList.contains('show'), false, 'the menu closes');
    assert.equal(page.document.activeElement, page.$('#suwayomiSearchInput'));
    assert.equal(status(page), '');
    assert.equal(sourceSearches(page).length, 0, 'nothing is searched until asked');
});

test('a read-only user is not offered the search', async () => {
    const page = await bootSearch({ user: { canModifyLibrary: false, canAdminister: false } });

    // main.css hides every [data-requires-write] control under this class.
    assert.ok(page.document.body.classList.contains('user-read-only'));
    for (const selector of ENTRY_POINTS) assert.ok(page.$(selector).hasAttribute('data-requires-write'), selector);
});

test('a library search that finds nothing offers to search Suwayomi for the same title', async () => {
    const page = await bootSearch({
        routes: { 'GET /api/files/series': libraryWithout() },
        found: { 102: [series(2, 'Chainsaw Man')] }
    });
    assert.equal(page.$('#fileList .suwayomi-search-offer'), null);

    await librarySearch(page, 'Chainsaw Man');
    assert.equal(text(page.$('#fileList .empty-state h2')), 'No series found');
    const offer = page.$('#fileList .empty-state .suwayomi-search-offer');
    assert.equal(text(offer), 'Search Suwayomi for “Chainsaw Man”');
    assert.equal(offer.hidden, false);
    assert.ok(offer.hasAttribute('data-requires-write'));

    page.click(offer);
    await page.settle();
    assert.ok(isOpen(page));
    assert.equal(page.$('#suwayomiSearchInput').value, 'Chainsaw Man');
    assert.deepEqual(librarySearches(page).map(call => call.query), [{ query: 'Chainsaw Man', limit: '10' }]);
    assert.deepEqual(sections(page), [{ title: 'MangaDex (EN)', series: ['Chainsaw Man'] }]);
});

test('the offer carries a title with quotes and markup into the dialog unchanged', async () => {
    const term = `Tom's "<b>Diary</b>"`;
    const page = await bootSearch({ routes: { 'GET /api/files/series': libraryWithout() } });

    await librarySearch(page, term);
    const offer = page.$('#fileList .suwayomi-search-offer');
    assert.equal(text(offer), `Search Suwayomi for “${term}”`);
    assert.equal(offer.querySelector('b'), null);

    page.click(offer);
    await page.settle();
    assert.equal(page.$('#suwayomiSearchInput').value, term);
    assert.equal(librarySearches(page)[0].query.query, term);
    assert.deepEqual(sourceSearches(page).map(call => call.query.query), [term, term]);
});

test('the offer stays hidden while Suwayomi is unavailable, and appears when the probe finds it', async () => {
    const probe = deferred();
    const page = await bootSearch({
        routes: { 'GET /api/files/series': libraryWithout(), 'GET /api/suwayomi/status': () => probe.promise }
    });

    await librarySearch(page, 'Saga');
    const offer = page.$('#fileList .suwayomi-search-offer');
    assert.equal(offer.hidden, true);

    probe.resolve(CONNECTED);
    await page.settle();
    assert.equal(offer.hidden, false);
    for (const selector of ENTRY_POINTS) assert.equal(page.$(selector).hidden, false, selector);
});

test('the actions are withdrawn when a later probe finds the server gone', async () => {
    const page = await bootSearch({ routes: { 'GET /api/files/series': libraryWithout() } });
    await librarySearch(page, 'Saga');
    assert.equal(page.$('#fileList .suwayomi-search-offer').hidden, false);

    // Settings > Test Connection probes again.
    page.route('GET /api/suwayomi/status', { ...CONNECTED, connected: false, error: 'Connection refused' });
    await page.run('testSuwayomiConnection()');
    await page.settle();

    for (const selector of [...ENTRY_POINTS, '#fileList .suwayomi-search-offer']) {
        assert.equal(page.$(selector).hidden, true, selector);
    }
});

test('without a search term the library offers nothing', async () => {
    const page = await bootSearch({ routes: { 'GET /api/files/series': seriesPage([]) } });

    assert.equal(text(page.$('#fileList .empty-state h2')), 'No series found');
    assert.equal(page.$('#fileList .suwayomi-search-offer'), null);
});

test('opened from the menu, the dialog starts from the library search term without searching yet', async () => {
    const page = await bootSearch({ routes: { 'GET /api/files/series': libraryWithout() } });
    await librarySearch(page, '  Saga ');

    page.click('#suwayomiSearchMenuItem');
    await page.settle();
    assert.equal(page.$('#suwayomiSearchInput').value, 'Saga');
    assert.equal(sourceSearches(page).length, 0);
    assert.equal(librarySearches(page).length, 0);
    assert.equal(requests(page, 'GET', '/api/suwayomi/sources').length, 1, 'the sources load for the language picker');
});

test('the Suwayomi library is listed first, then each source with the series not already shown', async () => {
    const page = await bootSearch({
        library: [series(1, 'Saga', { inLibrary: true })],
        found: {
            101: [series(3, 'Sagaland')],
            102: [series(1, 'Saga', { inLibrary: true }), series(2, 'Saga of the Swamp Thing'), series(2, 'Saga of the Swamp Thing')],
            103: [series(9, 'サーガ')]
        }
    });

    await search(page, 'Saga');

    assert.deepEqual(sections(page), [
        { title: 'In your Suwayomi library', series: ['Saga'] },
        { title: 'Comick (ALL)', series: ['Sagaland'] },
        { title: 'MangaDex (EN)', series: ['Saga of the Swamp Thing'] }
    ]);
    assert.equal(status(page), 'Found 3 series on 2 sources.');
    assert.equal(results(page).hasAttribute('aria-busy'), false);
    assert.equal(failures(page).hidden, true);

    // The browser's English picks the English and the multi-language sources.
    assert.equal(languageSelect(page).value, 'en');
    assert.deepEqual(searchedSourceIds(page), ['101', '102']);
    assert.deepEqual(librarySearches(page).map(call => call.query), [{ query: 'Saga', limit: '10' }]);
    for (const call of [...librarySearches(page), ...sourceSearches(page)]) {
        assert.deepEqual(call.query.query, 'Saga');
        assert.match(call.headers.authorization, /^Bearer /);
        assert.equal(call.init.credentials, 'same-origin');
    }

    const inLibrary = item(page, 1);
    assert.equal(inLibrary.dataset.inLibrary, 'true');
    assert.equal(inLibrary.querySelector('.suwayomi-search-badge').hidden, false);
    assert.equal(text(addButton(page, 1)), 'Download missing');

    const notInLibrary = item(page, 2);
    assert.equal(notInLibrary.dataset.inLibrary, 'false');
    assert.equal(notInLibrary.querySelector('.suwayomi-search-badge').hidden, true);
    const button = addButton(page, 2);
    assert.equal(text(button), 'Add & download');
    assert.ok(button.hasAttribute('data-requires-write'));
    assert.equal(text(page.$(`#${button.getAttribute('aria-describedby')}`)), 'Saga of the Swamp Thing');
});

test('answers that are not series are left out', async () => {
    const page = await bootSearch({
        found: { 102: [null, { id: 'x', title: 'Text id' }, { id: 0, title: 'Zero' }, { id: 1.5, title: 'Fraction' }, { id: 4 }, series(5, 'Saga'), 'Saga'] },
        routes: { 'GET /api/suwayomi/sources/101/series': { hasNextPage: false } }
    });

    await search(page, 'Saga');

    assert.deepEqual(sections(page), [{ title: 'MangaDex (EN)', series: ['Saga'] }]);
    assert.equal(status(page), 'Found 1 series on 2 sources.');
});

test('a series is shown with its cover, fetched through the signed-in thumbnail proxy', async () => {
    const page = await bootSearch({
        found: { 102: [series(2, 'Saga', { thumbnailUrl: '/api/v1/manga/2/thumbnail' }), series(3, 'Sagaland')] },
        routes: { 'GET /api/suwayomi/series/2/thumbnail': respond(200, 'image-bytes', { 'Content-Type': 'image/jpeg' }) }
    });
    await search(page, 'Saga');

    const cover = item(page, 2).querySelector('.suwayomi-search-cover img');
    assert.equal(cover.dataset.protectedImage, '/api/suwayomi/series/2/thumbnail');
    assert.equal(cover.getAttribute('alt'), '');
    assert.equal(item(page, 2).querySelector('.suwayomi-search-cover').getAttribute('aria-hidden'), 'true');
    assert.equal(item(page, 3).querySelector('img'), null, 'a series without a cover has no image');

    imageObserver(page).trigger(true, [cover]);
    await page.settle();
    const [call] = requests(page, 'GET', '/api/suwayomi/series/2/thumbnail');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.match(cover.src, /^blob:/);
});

test('sources are searched four at a time, after the Suwayomi library, and counted off as they answer', async () => {
    const sources = Array.from({ length: 6 }, (_, index) => source(String(201 + index), `Source ${index + 1}`, 'en'));
    const library = deferred();
    const answers = new Map(sources.map(item => [item.id, deferred()]));
    const page = await bootSearch({
        sources,
        routes: {
            'GET /api/suwayomi/series': () => library.promise,
            ...Object.fromEntries(sources.map(item => [`GET /api/suwayomi/sources/${item.id}/series`, () => answers.get(item.id).promise]))
        }
    });

    await search(page, 'Saga');
    assert.equal(status(page), 'Searching 6 sources...');
    assert.equal(results(page).getAttribute('aria-busy'), 'true');
    assert.deepEqual(searchedSourceIds(page), [], 'a series in the Suwayomi library is listed there, not under its source');

    library.resolve({ series: [] });
    await page.settle();
    assert.deepEqual(searchedSourceIds(page), ['201', '202', '203', '204']);

    answers.get('202').resolve({ series: [series(5, 'Saga')] });
    await page.settle();
    assert.equal(status(page), 'Searched 1 of 6 sources...');
    assert.deepEqual(searchedSourceIds(page), ['201', '202', '203', '204', '205']);
    assert.deepEqual(sections(page), [{ title: 'Source 2 (EN)', series: ['Saga'] }]);

    for (const id of ['201', '203', '204', '205', '206']) answers.get(id).resolve({ series: [] });
    await page.settle();
    assert.equal(status(page), 'Found 1 series on 6 sources.');
    assert.equal(results(page).hasAttribute('aria-busy'), false);
});

test('a failed search is listed with its reason while the other sources still show their series', async () => {
    const page = await bootSearch({
        found: { 102: [series(2, 'Saga')] },
        routes: {
            'GET /api/suwayomi/series': () => { throw new TypeError('Failed to fetch'); },
            'GET /api/suwayomi/sources/101/series': respond(400, { error: 'Comick answered HTTP 503' }, { 'X-Correlation-Id': 'corr-1' })
        }
    });

    await search(page, 'Saga');

    assert.deepEqual(sections(page), [{ title: 'MangaDex (EN)', series: ['Saga'] }]);
    assert.equal(status(page), 'Found 1 series on 2 sources. 2 searches failed.');
    assert.equal(failures(page).hidden, false);
    assert.equal(text(failures(page).querySelector('summary')), '2 searches failed');
    assert.deepEqual([...failures(page).querySelectorAll('li')].map(text), [
        'Suwayomi library: Failed to fetch',
        'Comick (ALL): Comick answered HTTP 503 (ref: corr-1)'
    ]);
    assert.equal(results(page).hasAttribute('aria-busy'), false);
});

test('the status sums up what the search found', async () => {
    const page = await bootSearch();
    const describe = (...args) => page.evaluate(`describeSuwayomiSearch(${args.map(arg => JSON.stringify(arg)).join(', ')})`);

    assert.equal(describe(0, 1, 'en', 0), 'No series found on 1 source.');
    assert.equal(describe(1, 3, '*', 1), 'Found 1 series on 3 sources. One search failed.');
    assert.equal(describe(0, 0, '*', 0), 'The Suwayomi server has no sources to search; install an extension there first.');
    assert.equal(describe(0, 0, 'ja', 0), 'The Suwayomi server has no Japanese sources to search.');
    assert.equal(describe(2, 0, '*', 0), 'Found 2 series in the Suwayomi library. The Suwayomi server has no sources to search; install an extension there first.');
});

test('a server without sources still searches its library', async () => {
    const page = await bootSearch({ sources: [], library: [series(1, 'Saga', { inLibrary: true })] });

    await search(page, 'Saga');

    assert.deepEqual(optionTexts(languageSelect(page)), ['All languages (0)']);
    assert.deepEqual(sections(page), [{ title: 'In your Suwayomi library', series: ['Saga'] }]);
    assert.equal(status(page), 'Found 1 series in the Suwayomi library. The Suwayomi server has no sources to search; install an extension there first.');
});

test('a title without letters or digits searches only the sources, as it would match the whole library', async () => {
    const page = await bootSearch();

    await search(page, '!?');

    assert.equal(librarySearches(page).length, 0);
    assert.deepEqual(searchedSourceIds(page), ['101', '102']);
    assert.equal(status(page), 'No series found on 2 sources.');
});

test('searching without a title asks for one', async () => {
    const page = await bootSearch();

    await search(page, '   ');

    assert.equal(status(page), 'Enter the title of a series to search for.');
    assert.equal(sourceSearches(page).length, 0);
    assert.equal(librarySearches(page).length, 0);
    assert.equal(results(page).children.length, 0);
});

test('the language picker counts each language\'s sources and starts on the browser\'s language', async () => {
    const page = await bootSearch({
        languages: ['de-DE', 'JA-jp', 'en'],
        sources: [
            ...SOURCES,
            source('104', 'Batoto', 'en'),
            source('105', 'Japscan', 'fr'),
            source('106', 'Oddity', 'not a tag'),
            { id: '107', name: 'Unlabelled', lang: '', displayName: 'Unlabelled' },
            // Not sources the server would list: they are ignored.
            { id: 108, name: 'Numeric id', lang: 'en', displayName: 'Numeric id' },
            { id: '-1', name: 'Negative', lang: 'en', displayName: 'Negative' },
            null
        ]
    });

    await openFromSidebar(page);

    // Multi-language sources count towards (and are searched for) every language.
    assert.deepEqual(optionTexts(languageSelect(page)), ['All languages (7)', 'English (3)', 'French (2)', 'Japanese (2)', 'not a tag (2)']);
    assert.equal(languageSelect(page).value, 'ja', 'the first browser language with sources, by its base language');

    await search(page, 'Saga');
    assert.deepEqual(searchedSourceIds(page), ['101', '103']);
});

test('without a source in a browser language, every source is searched', async () => {
    const page = await bootSearch({ languages: ['ko-KR'] });

    await search(page, 'Saga');

    assert.equal(languageSelect(page).value, '*');
    assert.deepEqual(searchedSourceIds(page), ['101', '102', '103']);
    assert.equal(status(page), 'No series found on 3 sources.');
});

test('a picked language is remembered and searched again straight away', async () => {
    const page = await bootSearch({ found: { 103: [series(9, 'Saga Raw')] } });

    await openFromSidebar(page);
    page.input('#suwayomiSearchLanguage', '*');
    await page.settle();
    assert.equal(page.window.localStorage.getItem('suwayomiSearchLanguage'), '*');
    assert.equal(sourceSearches(page).length, 0, 'nothing is searched before a title is');

    await search(page, 'Saga');
    assert.deepEqual(searchedSourceIds(page), ['101', '102', '103']);

    page.input('#suwayomiSearchLanguage', 'ja');
    await page.settle();
    assert.equal(page.window.localStorage.getItem('suwayomiSearchLanguage'), 'ja');
    assert.deepEqual(searchedSourceIds(page), ['101', '102', '103', '101', '103']);
    assert.deepEqual(sections(page), [{ title: 'Rawkuma (JA)', series: ['Saga Raw'] }]);

    // The next opening of the dialog starts on it, though the browser prefers English.
    page.click('#suwayomiSearchModal .close-btn');
    await openFromSidebar(page);
    assert.equal(languageSelect(page).value, 'ja');

    // A remembered language no longer installed falls back to the browser's.
    const later = await bootSearch({ sources: SOURCES.filter(item => item.lang !== 'ja'), localStorage: { suwayomiSearchLanguage: 'ja' } });
    await openFromSidebar(later);
    assert.equal(languageSelect(later).value, 'en');
});

test('sources that cannot be listed are reported in the dialog and asked for again by the next search', async () => {
    const page = await bootSearch({
        routes: { 'GET /api/suwayomi/sources': respond(400, { error: 'The Suwayomi integration is disabled' }, { 'X-Correlation-Id': 'corr-9' }) }
    });

    await openFromSidebar(page);
    assert.equal(status(page), 'Could not load the Suwayomi sources: The Suwayomi integration is disabled (ref: corr-9)');

    page.route('GET /api/suwayomi/sources', 500);
    await search(page, 'Saga');
    assert.equal(status(page), 'Could not load the Suwayomi sources: HTTP error! status: 500');
    assert.equal(results(page).hasAttribute('aria-busy'), false);
    assert.equal(librarySearches(page).length, 0);

    page.route('GET /api/suwayomi/sources', { sources: SOURCES });
    page.click('#suwayomiSearchButton');
    await page.settle();
    assert.equal(requests(page, 'GET', '/api/suwayomi/sources').length, 3);
    assert.equal(status(page), 'No series found on 2 sources.');
});

test('a sources list asked for by an earlier opening of the dialog is ignored', async () => {
    const answers = [deferred(), deferred(), deferred()];
    const [failing, outdated, current] = answers;
    const page = await bootSearch({ routes: { 'GET /api/suwayomi/sources': () => answers.shift().promise } });

    await openFromSidebar(page);
    page.click('#suwayomiSearchModal .close-btn');
    await openFromSidebar(page);
    page.click('#suwayomiSearchModal .close-btn');
    await openFromSidebar(page);

    failing.resolve(respond(500, { error: 'An outdated failure' }));
    outdated.resolve({ sources: [source('900', 'Outdated', 'fr')] });
    await page.settle();
    assert.equal(status(page), '');
    assert.deepEqual(optionTexts(languageSelect(page)), ['All languages'], 'not filled in yet');

    current.resolve({ sources: SOURCES });
    await page.settle();
    assert.deepEqual(optionTexts(languageSelect(page)), ['All languages (3)', 'English (2)', 'Japanese (2)']);
});

test('closing the dialog abandons its search: requests are cancelled and late answers dropped', async () => {
    const late = deferred();
    const page = await bootSearch({ routes: { 'GET /api/suwayomi/sources/102/series': () => late.promise } });
    await search(page, 'Saga');
    const [pending] = sourceSearches(page).filter(call => call.path.endsWith('/102/series'));
    assert.equal(status(page), 'Searched 1 of 2 sources...');

    press(page, 'Escape');
    await page.settle();
    assert.equal(isOpen(page), false);
    assert.equal(pending.init.signal.aborted, true);
    assert.equal(results(page).hasAttribute('aria-busy'), false);

    late.resolve({ series: [series(9, 'Saga')] });
    await page.settle();
    assert.equal(item(page, 9), null);
    assert.equal(status(page), 'Searched 1 of 2 sources...');

    // Opening it again starts afresh, with the sources listed anew.
    await openFromSidebar(page);
    assert.equal(results(page).children.length, 0);
    assert.equal(status(page), '');
    assert.equal(requests(page, 'GET', '/api/suwayomi/sources').length, 2);
});

test('a new search replaces the one still running', async () => {
    const slow = deferred();
    const page = await bootSearch({
        found: { 101: [series(3, 'Berserk')] },
        routes: {
            'GET /api/suwayomi/sources/102/series': call => (call.query.query === 'Saga' ? slow.promise : { series: [series(4, 'Berserk of Gluttony')] })
        }
    });

    await search(page, 'Saga');
    const [first] = sourceSearches(page).filter(call => call.path.endsWith('/102/series'));
    await search(page, 'Berserk');
    assert.equal(first.init.signal.aborted, true);

    slow.resolve({ series: [series(2, 'Saga')] });
    await page.settle();
    assert.deepEqual(sections(page), [
        { title: 'Comick (ALL)', series: ['Berserk'] },
        { title: 'MangaDex (EN)', series: ['Berserk of Gluttony'] }
    ]);
    assert.equal(status(page), 'Found 2 series on 2 sources.');
});

test('adding a series puts it in the Suwayomi library and queues the issues the library lacks', async () => {
    const answer = deferred();
    const page = await bootSearch({
        found: { 102: [series(2, 'Saga Deluxe')] },
        routes: { 'POST /api/suwayomi/series/2/add': () => answer.promise }
    });
    await search(page, 'Saga');
    const button = addButton(page, 2);

    page.click(button);
    await page.settle();
    assert.equal(button.disabled, true);
    assert.equal(text(button), 'Adding...');
    const [call] = requests(page, 'POST', '/api/suwayomi/series/2/add');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.equal(call.body, undefined);

    answer.resolve(addResult({ title: 'Saga Deluxe', localSeriesTitle: 'Saga', chapterCount: 10, queued: 3, owned: 7 }));
    await page.settle();
    assert.deepEqual(dialogMessages(page), [{
        type: 'success',
        text: 'Added "Saga Deluxe" to the Suwayomi library and queued 3 chapters for download. 7 issues are already in your library as "Saga".',
        role: null
    }]);
    assert.deepEqual(messages(page), []);
    assert.equal(button.disabled, false);
    assert.equal(text(button), 'Download missing');
    assert.equal(item(page, 2).dataset.inLibrary, 'true');
    assert.equal(item(page, 2).querySelector('.suwayomi-search-badge').hidden, false);
});

test('a series listed again while its add is pending shows the outcome on the new row too', async () => {
    const first = deferred();
    const answers = [first.promise, new Promise(() => {})];
    const page = await bootSearch({
        found: { 102: [series(2, 'Saga')] },
        routes: { 'POST /api/suwayomi/series/2/add': () => answers.shift() }
    });
    await search(page, 'Saga');
    page.click(addButton(page, 2));
    await page.settle();

    await search(page, 'Saga');
    assert.equal(text(addButton(page, 2)), 'Add & download');
    first.resolve(addResult({ chapterCount: 2, queued: 2 }));
    await page.settle();
    assert.equal(item(page, 2).dataset.inLibrary, 'true');
    assert.equal(text(addButton(page, 2)), 'Download missing');

    // A row with its own add under way keeps its in-progress label.
    const rival = deferred();
    answers.unshift(rival.promise);
    await search(page, 'Saga');
    const pending = addButton(page, 2);
    page.click(pending);
    await page.settle();
    assert.equal(text(pending), 'Adding...');
    page.evaluate('markSuwayomiSeriesInLibrary(2)');
    assert.equal(text(pending), 'Adding...');
});

test('a series already in the Suwayomi library only has its missing chapters queued', async () => {
    const answer = deferred();
    const page = await bootSearch({
        library: [series(1, 'Saga', { inLibrary: true })],
        routes: { 'POST /api/suwayomi/series/1/add': () => answer.promise }
    });
    await search(page, 'Saga');
    const button = addButton(page, 1);

    page.click(button);
    await page.settle();
    assert.equal(text(button), 'Queuing...');

    answer.resolve(addResult({ id: 1, alreadyInLibrary: true, chapterCount: 12, queued: 1, owned: 9, downloaded: 2 }));
    await page.settle();
    assert.deepEqual(dialogMessages(page).at(-1), {
        type: 'success',
        text: '"Saga" is already in the Suwayomi library; queued 1 chapter for download. 9 issues are already in your library. 2 chapters were already downloaded.',
        role: null
    });
    assert.equal(text(button), 'Download missing');
});

test('the message after adding explains why nothing was queued', async () => {
    const page = await bootSearch();
    const describe = result => page.evaluate(`describeSuwayomiAddResult(${JSON.stringify(result)})`);

    assert.equal(describe(addResult({ alreadyInLibrary: true, chapterCount: 4, owned: 1, downloaded: 1, unnumbered: 2 })),
        '"Saga" is already in the Suwayomi library; there is nothing new to download. 1 issue is already in your library. 1 chapter was already downloaded. 2 unnumbered chapters were skipped.');
    assert.equal(describe(addResult({ title: 'Oneshot' })), 'Added "Oneshot" to the Suwayomi library; its source lists no chapters yet.');
    assert.equal(describe(addResult({ chapterCount: 3, queued: 1, unnumbered: 1 })),
        'Added "Saga" to the Suwayomi library and queued 1 chapter for download. 1 unnumbered chapter was skipped.');
    assert.equal(describe({}), 'Added "The series" to the Suwayomi library; its source lists no chapters yet.');
});

test('a series already in the Suwayomi library with nothing to queue is reported as information', async () => {
    const page = await bootSearch({
        library: [series(1, 'Saga', { inLibrary: true })],
        routes: { 'POST /api/suwayomi/series/1/add': addResult({ id: 1, alreadyInLibrary: true, chapterCount: 3, owned: 3 }) }
    });
    await search(page, 'Saga');

    page.click(addButton(page, 1));
    await page.settle();

    assert.deepEqual(dialogMessages(page).at(-1), {
        type: 'info',
        text: '"Saga" is already in the Suwayomi library; there is nothing new to download. 3 issues are already in your library.',
        role: null
    });
});

test('a failed add says why and leaves the series as it was', async () => {
    const page = await bootSearch({ found: { 102: [series(2, 'Saga')] } });
    await search(page, 'Saga');
    const button = addButton(page, 2);
    const failuresAndMessages = [
        [respond(400, { error: "Added 'Saga' to the Suwayomi library, but its chapters could not be queued: timed out" }, { 'X-Correlation-Id': 'corr-2' }),
            "Added 'Saga' to the Suwayomi library, but its chapters could not be queued: timed out (ref: corr-2)"],
        [500, 'Could not add the series on Suwayomi: HTTP error! status: 500'],
        [() => { throw new TypeError('Failed to fetch'); }, 'Could not add the series on Suwayomi: Failed to fetch']
    ];

    for (const [failure, message] of failuresAndMessages) {
        page.route('POST /api/suwayomi/series/2/add', failure);
        page.click(button);
        await page.settle();
        assert.deepEqual(dialogMessages(page).at(-1), { type: 'error', text: message, role: 'alert' });
        assert.equal(button.disabled, false);
        assert.equal(text(button), 'Add & download');
        assert.equal(item(page, 2).dataset.inLibrary, 'false');
    }
    assert.deepEqual(messages(page), []);
    assert.equal(consoleLines(page, 'error').filter(line => line.startsWith('Adding the Suwayomi series failed')).length, 3);
});

test('an add answered after the dialog closed is reported on the page, and reopening the dialog clears earlier reports', async () => {
    const late = deferred();
    const page = await bootSearch({
        found: { 102: [series(2, 'Saga'), series(3, 'Saga Deluxe')] },
        routes: {
            'POST /api/suwayomi/series/2/add': addResult({ chapterCount: 2, queued: 2 }),
            'POST /api/suwayomi/series/3/add': () => late.promise
        }
    });
    await search(page, 'Saga');
    page.click(addButton(page, 2));
    await page.settle();
    assert.deepEqual(dialogMessages(page).map(message => message.text), ['Added "Saga" to the Suwayomi library and queued 2 chapters for download.']);

    page.click(addButton(page, 3));
    await page.settle();
    press(page, 'Escape');
    await page.settle();
    assert.equal(isOpen(page), false);
    late.resolve(addResult({ id: 3, title: 'Saga Deluxe' }));
    await page.settle();
    assert.deepEqual(messages(page), [{ type: 'success', text: 'Added "Saga Deluxe" to the Suwayomi library; its source lists no chapters yet.', role: null }]);
    assert.equal(dialogMessages(page).length, 1);

    await openFromSidebar(page);
    assert.deepEqual(dialogMessages(page), []);
});

test('an expired session goes to the login page, from searching and from adding', async () => {
    const listing = await bootSearch({ routes: { 'GET /api/suwayomi/sources': 401 } });
    await search(listing, 'Saga');
    assert.ok(wentToLogin(listing));
    assert.equal(status(listing), '');
    assert.equal(sourceSearches(listing).length, 0);

    const searching = await bootSearch({ routes: { 'GET /api/suwayomi/sources/102/series': 401 } });
    await search(searching, 'Saga');
    assert.ok(wentToLogin(searching));
    assert.equal(status(searching), '');
    assert.equal(failures(searching).hidden, true);
    assert.equal(results(searching).hasAttribute('aria-busy'), false);

    const adding = await bootSearch({ found: { 102: [series(2, 'Saga')] }, routes: { 'POST /api/suwayomi/series/2/add': 401 } });
    await search(adding, 'Saga');
    adding.click(addButton(adding, 2));
    await adding.settle();
    assert.ok(wentToLogin(adding));
    assert.deepEqual(messages(adding), []);
    assert.deepEqual(dialogMessages(adding), []);
});
