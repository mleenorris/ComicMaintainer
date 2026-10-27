'use strict';

// Searching the external providers from the Manage Series Names modal
// (js/app/series-metadata.js): the candidate list, adopting a candidate's
// names as user aliases or as the series name, and applying a candidate as
// the series' correct external match.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootSeries, metadataRecord, lastMessage, callsTo, text, libraryRefreshes, openManageNames, optionTexts, buttonIn,
    answerConfirm
} = require('./support/series-helpers');

const MODAL = '#manageSeriesNamesModal';
const SEARCH = '/api/metadata/search';
const APPLY = '/api/metadata/series/Saga/apply-match';
const NAME = '/api/metadata/series/Saga/name';
const NO_MATCHES = 'No matches found. Check that external metadata providers are enabled in Settings.';
const offline = () => { throw new TypeError('Failed to fetch'); };

const nameSelect = page => page.$('#manageSeriesName');
const resultsBox = page => page.$('#manageSeriesSearchResults');
const resultCards = page => [...resultsBox(page).children];
const userAliases = page => page.$$('#manageSeriesUserAliases .badge').map(badge => text(badge).replace(/\s*×$/, ''));
const logged = (page, label) => page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === label);

async function search(page, query) {
    if (query !== undefined) page.input('#manageSeriesSearchInput', query);
    page.click(`${MODAL} button[onclick="searchExternalSeries()"]`);
    await page.settle();
}

// Clicks the button labelled `label` on the `index`th search result.
async function onResult(page, index, label) {
    const button = [...resultCards(page)[index].querySelectorAll('button')].find(el => text(el) === label);
    if (!button) throw new Error(`No "${label}" button on result ${index}`);
    page.click(button);
    await page.settle();
}

// What a rendered search result shows.
function describe(card) {
    const [header, aliases] = card.children;
    const score = card.querySelector('span[title="Confidence that this is the right match"]');
    return {
        title: text(card.querySelector('strong')),
        score: score && text(score),
        tone: score && /(?:^|; )color: ([^;]+);/.exec(score.getAttribute('style'))[1],
        source: text(header.children[1].lastElementChild),
        aliases: text(aliases),
        buttons: [...card.querySelectorAll('button')].map(text)
    };
}

// GET /api/metadata/search candidates (SeriesSearchResult, snake_case).
const RESULTS = [
    {
        canonical_title: 'Saga (Image Comics)',
        source: 'ComicVine',
        match_score: 96.5,
        aliases: ['サーガ', 'Saga Deluxe'],
        image_url: 'https://comicvine.example/saga.jpg',
        localized_titles: [
            { title: 'サーガ', language: 'ja' },
            { title: 'Saga (Image Comics)', language: 'en' },
            null,
            { title: '', language: 'ko' },
            { title: 'Saga Deluxe' }
        ]
    },
    {
        canonical_title: 'Saga of the Swamp Thing',
        source: 'MangaDex',
        match_score: 72,
        aliases: [],
        thumbnail_url: 'https://mangadex.example/swamp-thing.256.jpg'
    },
    { canonical_title: 'SAGA', match_score: 12 },
    { source: 'AniList', aliases: ['Untitled Saga'] }
];

async function searchedPage(record = metadataRecord(), options = {}) {
    const page = await bootSeries(options);
    await openManageNames(page, record);
    page.route(`GET ${SEARCH}`, { results: RESULTS });
    await search(page);
    return page;
}

test('an empty search asks for a query instead of calling the providers', async () => {
    const page = await bootSeries();
    await openManageNames(page);

    for (const query of ['', '   ']) {
        resultsBox(page).textContent = '';
        await search(page, query);
        assert.equal(text(resultsBox(page)), 'Enter a query above to search.', JSON.stringify(query));
    }
    assert.equal(callsTo(page, SEARCH).length, 0);
});

test('result actions do nothing before a search has listed any results', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ user_aliases: ['My Saga'] }));
    const requests = page.fetchCalls.length;

    page.run('adoptSearchResult(0, \'aliases\'); applySearchResultAsMatch(0);');
    await page.settle();

    assert.deepEqual(userAliases(page), ['My Saga']);
    assert.deepEqual(page.dialogs, []);
    assert.equal(page.fetchCalls.length, requests);
});

test('a provider search lists each candidate with its score, source and aliases', async () => {
    const page = await bootSeries();
    await openManageNames(page);
    const pending = deferred();
    page.route(`GET ${SEARCH}`, () => pending.promise);

    await search(page, '  Saga Deluxe  ');

    assert.equal(text(resultsBox(page)), 'Searching...');
    const [call] = callsTo(page, SEARCH, 'GET');
    assert.deepEqual({ ...call.query }, { query: 'Saga Deluxe', limit: '10' });

    pending.resolve({ results: RESULTS });
    await page.settle();

    const actions = ['Use This Match', 'Adopt as Canonical', 'Add Aliases'];
    assert.deepEqual(resultCards(page).map(describe), [
        {
            title: 'Saga (Image Comics) en',
            score: '96.5% match',
            tone: 'var(--accent-success, #1f9d55)',
            source: 'ComicVine',
            aliases: 'Aliases: サーガ ja, Saga Deluxe',
            buttons: actions
        },
        {
            title: 'Saga of the Swamp Thing',
            score: '72.0% match',
            tone: 'var(--accent-warning, #c69026)',
            source: 'MangaDex',
            aliases: 'Aliases: none',
            buttons: actions
        },
        {
            title: 'SAGA',
            score: '12.0% match',
            tone: 'var(--text-secondary)',
            source: '',
            aliases: 'Aliases: none',
            buttons: actions
        },
        { title: '', score: null, tone: null, source: 'AniList', aliases: 'Aliases: Untitled Saga', buttons: actions }
    ]);
    assert.equal(resultCards(page)[1].querySelector('em').textContent, 'none');
});

test('failed and empty provider searches say so', async () => {
    const page = await bootSeries();
    await openManageNames(page);
    const outcomes = [
        [respond(500, { error: 'Provider error' }), 'Search failed.'],
        [offline, 'Search failed.'],
        [{ results: [] }, NO_MATCHES],
        [{}, NO_MATCHES]
    ];

    for (const [handler, expected] of outcomes) {
        page.route(`GET ${SEARCH}`, handler);
        await search(page);
        assert.equal(text(resultsBox(page)), expected);
    }
    assert.equal(callsTo(page, SEARCH, 'GET').length, outcomes.length);
    assert.ok(logged(page, 'searchExternalSeries failed'));
});

test('Add Aliases copies a candidate\'s names into the user aliases without saving them', async () => {
    const page = await searchedPage(metadataRecord({ user_aliases: ['My Saga'] }));

    await onResult(page, 0, 'Add Aliases');

    assert.deepEqual(userAliases(page), ['My Saga', 'サーガ', 'Saga Deluxe', 'Saga (Image Comics)']);
    assert.deepEqual(optionTexts(nameSelect(page)).slice(1), [
        'Saga (canonical)',
        'My Saga (your alias)',
        'サーガ (your alias)',
        'Saga Deluxe (your alias)',
        'Saga (Image Comics) (your alias)'
    ]);
    assert.equal(nameSelect(page).value, '', 'the series name is left alone');

    await onResult(page, 0, 'Add Aliases');
    await onResult(page, 3, 'Add Aliases');
    assert.deepEqual(userAliases(page), ['My Saga', 'サーガ', 'Saga Deluxe', 'Saga (Image Comics)', 'Untitled Saga'],
        'names already listed are not repeated');

    page.run('adoptSearchResult(99, \'aliases\')');
    assert.equal(userAliases(page).length, 5, 'an unknown result changes nothing');
    assert.equal(page.fetchCalls.filter(call => call.method === 'PUT').length, 0);

    page.route('PUT /api/metadata/series/Saga/aliases', call => metadataRecord({ user_aliases: call.body.aliases }));
    page.click(buttonIn(page, `${MODAL} .modal-footer`, 'Save'));
    await page.settle();
    assert.deepEqual(callsTo(page, '/api/metadata/series/Saga/aliases', 'PUT')[0].body, {
        aliases: ['My Saga', 'サーガ', 'Saga Deluxe', 'Saga (Image Comics)', 'Untitled Saga']
    });
});

test('Add Aliases starts the alias list of a series that has no user aliases', async () => {
    const page = await searchedPage(metadataRecord({ user_aliases: null }));
    assert.equal(text(page.$('#manageSeriesUserAliases')), 'None');

    await onResult(page, 1, 'Add Aliases');

    assert.deepEqual(userAliases(page), ['Saga of the Swamp Thing']);
    assert.equal(optionTexts(nameSelect(page)).at(-1), 'Saga of the Swamp Thing (your alias)');
});

test('Adopt as Canonical adds the candidate names as aliases and pins the candidate title as the series name', async () => {
    const page = await searchedPage(metadataRecord({ user_aliases: ['My Saga'] }));
    const pending = deferred();
    page.route(`PUT ${NAME}`, () => pending.promise);

    await onResult(page, 0, 'Adopt as Canonical');

    assert.deepEqual(userAliases(page), ['My Saga', 'Saga', 'Saga (Image Comics)', 'サーガ', 'Saga Deluxe']);
    assert.equal(nameSelect(page).value, 'Saga (Image Comics)');
    const [put] = callsTo(page, NAME, 'PUT');
    assert.deepEqual(put.body, { name: 'Saga (Image Comics)' });

    pending.resolve(metadataRecord({
        user_aliases: ['My Saga', 'Saga (Image Comics)'],
        series_name: 'Saga (Image Comics)',
        resolved_series_name: 'Saga (Image Comics)'
    }));
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Series name set to "Saga (Image Comics)"', type: 'success' });
    assert.equal(nameSelect(page).value, 'Saga (Image Comics)');
    assert.equal(text(page.$('#manageSeriesResolvedName')), 'Using your selected name: "Saga (Image Comics)".');
});

test('Adopt as Canonical selects a title that only differs in case and skips candidates without one', async () => {
    const page = await searchedPage();
    const pending = deferred();
    page.route(`PUT ${NAME}`, () => pending.promise);

    await onResult(page, 2, 'Adopt as Canonical');

    assert.deepEqual(userAliases(page), ['Saga']);
    assert.equal(nameSelect(page).value, 'SAGA');
    assert.equal(optionTexts(nameSelect(page)).at(-1), 'SAGA', 'the candidate spelling is offered as its own choice');
    assert.deepEqual(callsTo(page, NAME, 'PUT').map(call => call.body), [{ name: 'SAGA' }]);

    // The server matches the name case-insensitively and pins the known spelling.
    pending.resolve(metadataRecord({ series_name: 'Saga', resolved_series_name: 'Saga' }));
    await page.settle();
    assert.equal(nameSelect(page).value, 'Saga');
    assert.equal(text(page.$('#manageSeriesResolvedName')), 'Using your selected name: "Saga".');

    await onResult(page, 3, 'Adopt as Canonical');
    assert.deepEqual(userAliases(page), ['Saga', 'Untitled Saga']);
    assert.equal(callsTo(page, NAME, 'PUT').length, 1, 'no title, so the series name is not changed');
});

test('candidates can still be adopted when the series record failed to load', async () => {
    const page = await searchedPage(respond(500));

    await onResult(page, 1, 'Add Aliases');

    assert.deepEqual(userAliases(page), ['Saga of the Swamp Thing']);
    assert.deepEqual(optionTexts(nameSelect(page)), ['Automatic (use language preference)', 'Saga of the Swamp Thing (your alias)']);
});

test('Use This Match asks first, then replaces the cached provider metadata', async () => {
    const page = await searchedPage(metadataRecord({ user_aliases: ['My Saga'] }));
    page.route('GET /api/series-images/saga', respond(200, 'PNGDATA', { 'Content-Type': 'image/png' }));
    const refreshes = libraryRefreshes(page);
    const prompt = 'Mark "Saga (Image Comics)" (ComicVine) as the correct match for "Saga"? This replaces the cached provider metadata for this series.';

    answerConfirm(page, false);
    await onResult(page, 0, 'Use This Match');
    assert.deepEqual(page.dialogs.map(dialog => [dialog.kind, dialog.message]), [['confirm', prompt]]);
    assert.equal(callsTo(page, APPLY).length, 0);

    answerConfirm(page, true);
    page.route(`POST ${APPLY}`, call => metadataRecord({
        aliases: call.body.aliases,
        user_aliases: ['My Saga'],
        source: call.body.source,
        lookup_status: 'manual_match',
        localized_titles: call.body.localizedTitles,
        has_image: true
    }));
    await onResult(page, 0, 'Use This Match');

    const [post] = callsTo(page, APPLY, 'POST');
    assert.equal(post.headers['content-type'], 'application/json');
    assert.deepEqual(post.body, {
        canonicalTitle: 'Saga (Image Comics)',
        aliases: ['サーガ', 'Saga Deluxe'],
        source: 'ComicVine',
        imageUrl: 'https://comicvine.example/saga.jpg',
        thumbnailUrl: null,
        localizedTitles: [
            { title: 'サーガ', language: 'ja' },
            { title: 'Saga (Image Comics)', language: 'en' },
            { title: 'Saga Deluxe', language: null }
        ]
    });
    assert.deepEqual(lastMessage(page), { text: 'Match applied', type: 'success' });
    assert.deepEqual(page.$$('#manageSeriesProviderAliases .badge').map(text), ['サーガ ja', 'Saga Deluxe']);
    assert.match(text(page.$('#manageSeriesProviderSource')), /^Source: ComicVine · Last lookup: never · Status: manual_match$/);
    assert.deepEqual(userAliases(page), ['My Saga']);
    assert.deepEqual(optionTexts(nameSelect(page)), [
        'Automatic (use language preference)',
        'Saga (canonical)',
        'サーガ (ja)',
        'Saga (Image Comics) (en)',
        'Saga Deluxe',
        'My Saga (your alias)'
    ]);
    assert.equal(text(page.$('#manageSeriesImageStatus')), 'Downloaded from provider · unknown');
    assert.ok(page.objectUrls.has(page.$('#manageSeriesImagePreview').src));
    assert.equal(libraryRefreshes(page), refreshes + 1);
});

test('Use This Match sends what a sparse candidate has and ignores one without a title', async () => {
    const page = await searchedPage();
    page.route(`POST ${APPLY}`, metadataRecord({ lookup_status: 'manual_match' }));

    await onResult(page, 3, 'Use This Match');
    assert.deepEqual(page.dialogs, [], 'a candidate without a title cannot be applied');
    assert.equal(callsTo(page, APPLY).length, 0);

    await onResult(page, 2, 'Use This Match');
    assert.deepEqual(page.dialogs.map(dialog => dialog.message), [
        'Mark "SAGA" (unknown source) as the correct match for "Saga"? This replaces the cached provider metadata for this series.'
    ]);
    assert.deepEqual(callsTo(page, APPLY, 'POST')[0].body, {
        canonicalTitle: 'SAGA',
        aliases: [],
        source: null,
        imageUrl: null,
        thumbnailUrl: null,
        localizedTitles: []
    });

    await onResult(page, 1, 'Use This Match');
    assert.equal(callsTo(page, APPLY, 'POST')[1].body.thumbnailUrl, 'https://mangadex.example/swamp-thing.256.jpg');
    assert.deepEqual(lastMessage(page), { text: 'Match applied', type: 'success' });
});

test('a rejected match is reported with the server message when it sends one', async () => {
    const page = await searchedPage();
    const refreshes = libraryRefreshes(page);
    const outcomes = [
        [respond(400, { error: 'Candidate canonical title is required' }), 'Candidate canonical title is required'],
        [respond(400, '"Unknown provider"', { 'Content-Type': 'application/json' }), 'Unknown provider'],
        [respond(500, 'Error applying match'), 'Failed to apply match'],
        [offline, 'Failed to apply match']
    ];

    for (const [handler, expected] of outcomes) {
        page.route(`POST ${APPLY}`, handler);
        await onResult(page, 0, 'Use This Match');
        assert.deepEqual(lastMessage(page), { text: expected, type: 'error' });
    }
    assert.ok(logged(page, 'applySearchResultAsMatch failed'));
    assert.equal(libraryRefreshes(page), refreshes);
});
