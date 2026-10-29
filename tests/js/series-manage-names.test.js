'use strict';

// The Manage Series Names modal (js/app/series-metadata.js) opened from the
// series page's Actions menu: the Series Name and Preferred Display Language
// selects, provider and user aliases, Refresh from Provider, Clear External
// Metadata and Check for Combinable Folders.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred, addReadOnlyGate, isDisplayed } = require('./support/app');
const {
    bootSeries, metadataRecord, messages, lastMessage, callsTo, text, libraryRefreshes, openManageNames, optionTexts,
    buttonIn, answerConfirm
} = require('./support/series-helpers');

const MODAL = '#manageSeriesNamesModal';
const RECORD = '/api/metadata/series/Saga';
const AUTOMATIC = 'Automatic (use language preference)';
const NO_LOOKUP = 'No external lookup yet — use "Refresh from Provider" to populate.';
const CLEAR_PROMPT = 'Clear cached external metadata for "Saga"? User aliases and a user-uploaded image will be kept.';
const offline = () => { throw new TypeError('Failed to fetch'); };

const modalOpen = page => page.$(MODAL).classList.contains('active');
const nameSelect = page => page.$('#manageSeriesName');
const languageSelect = page => page.$('#manageSeriesPreferredLanguage');
const optionValues = select => [...select.options].map(option => option.value);
const footer = (page, label) => buttonIn(page, `${MODAL} .modal-footer`, label);
const providerAliases = page => page.$$('#manageSeriesProviderAliases .badge').map(text);
const localTime = (page, iso) => page.run(`new Date(${JSON.stringify(iso)}).toLocaleString()`);
const logged = (page, label) => page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === label);

// The user aliases listed in the modal (badge text without its × button).
const userAliasBadges = page => page.$$('#manageSeriesUserAliases .badge');
const aliasOf = badge => text(badge).replace(/\s*×$/, '');
const userAliases = page => userAliasBadges(page).map(aliasOf);

function removeButton(page, alias) {
    const badge = userAliasBadges(page).find(el => aliasOf(el) === alias);
    if (!badge) throw new Error(`No user alias "${alias}"`);
    return badge.querySelector('button');
}

function addAlias(page, value) {
    page.input('#manageSeriesNewAlias', value);
    page.click(buttonIn(page, MODAL, 'Add'));
}

async function press(page, label) {
    page.click(footer(page, label));
    await page.settle();
}

// A record with every kind of name: a canonical title, localized titles (with
// and without a language, plus unusable entries), provider aliases that
// repeat other names, and a user alias.
const RICH = metadataRecord({
    aliases: ['サーガ', 'Saga Comic', 'SAGA'],
    user_aliases: ['My Saga'],
    resolved_series_name: 'サーガ',
    source: 'MangaDex',
    last_lookup_utc: '2024-03-05T10:20:30Z',
    lookup_status: 'success',
    preferred_language: 'ja',
    localized_titles: [
        { title: 'サーガ', language: 'ja' },
        { title: 'Saga: Deluxe Edition', language: null },
        null,
        { title: '', language: 'ko' },
        { title: '   ', language: 'ko' },
        { title: 'saga', language: 'en' }
    ]
});

test('Manage Names loads the series record into the name, language and alias controls', async () => {
    const page = await bootSeries();
    const pending = deferred();

    await openManageNames(page, () => pending.promise);

    assert.ok(modalOpen(page));
    assert.equal(text(page.$('#manageSeriesTitle')), 'Saga');
    assert.equal(page.$('#manageSeriesSearchInput').value, 'Saga', 'the provider search is prefilled with the title');
    assert.equal(page.$('#manageSeriesSearchResults').children.length, 0);
    assert.equal(text(page.$('#manageSeriesProviderAliases')), 'Loading...');
    assert.equal(text(page.$('#manageSeriesUserAliases')), 'Loading...');
    assert.deepEqual(optionTexts(nameSelect(page)), [AUTOMATIC]);

    pending.resolve(RICH);
    await page.settle();

    assert.equal(callsTo(page, RECORD, 'GET').length, 1);
    assert.deepEqual(optionTexts(nameSelect(page)), [
        AUTOMATIC,
        'Saga (canonical)',
        'サーガ (ja)',
        'Saga: Deluxe Edition',
        'Saga Comic (provider alias)',
        'My Saga (your alias)'
    ], 'every distinct name is listed once, labelled with where it came from');
    assert.deepEqual(optionValues(nameSelect(page)), ['', 'Saga', 'サーガ', 'Saga: Deluxe Edition', 'Saga Comic', 'My Saga']);
    assert.equal(nameSelect(page).value, '');
    assert.equal(text(page.$('#manageSeriesResolvedName')), 'Automatic name currently resolves to: "サーガ".');
    assert.equal(languageSelect(page).value, 'ja');

    assert.deepEqual(providerAliases(page), ['サーガ ja', 'Saga Comic', 'SAGA en'], 'aliases carry their language');
    assert.equal(
        text(page.$('#manageSeriesProviderSource')),
        `Source: MangaDex · Last lookup: ${localTime(page, '2024-03-05T10:20:30Z')} · Status: success`
    );
    assert.deepEqual(userAliases(page), ['My Saga']);
    assert.equal(removeButton(page, 'My Saga').title, 'Remove alias');

    assert.equal(text(page.$('#manageSeriesImageStatus')), 'No image cached. Refresh metadata or upload one below.');
    assert.equal(page.$('#manageSeriesImagePreview').hasAttribute('src'), false);
    assert.equal(page.fetchCalls.filter(call => call.path.startsWith('/api/series-images')).length, 0);
});

test('reopening Manage Names starts from a clean slate and reflects the stored name choice', async () => {
    const page = await bootSeries();

    await openManageNames(page, metadataRecord({
        aliases: ['Saga Comic'],
        series_name: 'Saga (2012)',
        resolved_series_name: 'Saga (2012)',
        preferred_language: 'ko'
    }));

    assert.deepEqual(optionTexts(nameSelect(page)), [
        AUTOMATIC, 'Saga (canonical)', 'Saga Comic (provider alias)', 'Saga (2012)'
    ], 'a stored name that is not otherwise known stays selectable');
    assert.equal(nameSelect(page).value, 'Saga (2012)');
    assert.equal(text(page.$('#manageSeriesResolvedName')), 'Using your selected name: "Saga (2012)".');
    assert.equal(languageSelect(page).value, 'ko');

    page.click(`${MODAL} .close-btn`);
    assert.equal(modalOpen(page), false);

    const pending = deferred();
    await openManageNames(page, () => pending.promise);
    assert.ok(modalOpen(page));
    assert.deepEqual(optionTexts(nameSelect(page)), [AUTOMATIC], 'the previous names are gone while loading');
    assert.equal(nameSelect(page).value, '');
    assert.equal(text(page.$('#manageSeriesResolvedName')), '');
    assert.equal(text(page.$('#manageSeriesProviderSource')), '');

    pending.resolve(metadataRecord({
        aliases: ['Saga Comic'],
        series_name: 'Saga Comic',
        resolved_series_name: 'Saga Comic',
        source: 'ComicVine'
    }));
    await page.settle();
    assert.deepEqual(optionTexts(nameSelect(page)), [AUTOMATIC, 'Saga (canonical)', 'Saga Comic (provider alias)']);
    assert.equal(nameSelect(page).value, 'Saga Comic');
    assert.equal(text(page.$('#manageSeriesResolvedName')), 'Using your selected name: "Saga Comic".');
    assert.equal(text(page.$('#manageSeriesProviderSource')), 'Source: ComicVine · Last lookup: never · Status: unknown');
    assert.equal(languageSelect(page).value, '');

    await press(page, 'Close');
    assert.equal(modalOpen(page), false);

    await openManageNames(page, metadataRecord({
        canonical_title: null,
        aliases: null,
        user_aliases: null,
        localized_titles: null,
        resolved_series_name: null
    }));
    assert.deepEqual(optionTexts(nameSelect(page)), [AUTOMATIC]);
    assert.equal(text(page.$('#manageSeriesResolvedName')), '');
    assert.equal(text(page.$('#manageSeriesProviderAliases')), 'None');
    assert.equal(text(page.$('#manageSeriesProviderSource')), NO_LOOKUP);
    assert.equal(text(page.$('#manageSeriesUserAliases')), 'None');
    assert.equal(callsTo(page, RECORD, 'GET').length, 3);
});

test('a series record that cannot be loaded is reported and leaves nothing to save', async () => {
    const page = await bootSeries();

    await openManageNames(page, respond(500, { error: 'database is locked' }));

    assert.deepEqual(lastMessage(page), { text: 'Failed to load series metadata', type: 'error' });
    assert.equal(text(page.$('#manageSeriesProviderAliases')), 'Loading...');
    assert.equal(text(page.$('#manageSeriesUserAliases')), 'Loading...');
    assert.deepEqual(optionTexts(nameSelect(page)), [AUTOMATIC]);

    await press(page, 'Save');
    assert.equal(page.fetchCalls.filter(call => call.method === 'PUT').length, 0, 'there is no record to save');
    assert.ok(modalOpen(page));

    await press(page, 'Close');
    await openManageNames(page, offline);
    assert.deepEqual(lastMessage(page), { text: 'Failed to load series metadata', type: 'error' });
    assert.ok(logged(page, 'loadManageSeriesRecord failed'));
});

test('the controls of a closed Manage Names modal do nothing', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ user_aliases: ['My Saga'] }));
    await press(page, 'Close');
    const metadataCalls = () =>
        page.fetchCalls.filter(call => /^\/api\/(metadata|series-images)\//.test(call.path)).length;
    const before = metadataCalls();

    page.input(languageSelect(page), 'ja');
    page.input(nameSelect(page), 'Saga');
    page.click(removeButton(page, 'My Saga'));
    page.$('#manageSeriesImageInput').dispatchEvent(new page.window.Event('change'));
    for (const label of ['Save', 'Refresh from Provider', 'Clear External Metadata']) page.click(footer(page, label));
    page.run('loadManageSeriesRecord(); applySearchResultAsMatch(0);');
    await page.settle();

    assert.equal(metadataCalls(), before);
    assert.deepEqual(page.dialogs, []);
    assert.deepEqual(userAliases(page), ['My Saga']);
    assert.equal(messages(page).length, 0);
});

test('changing the preferred language saves it straight away', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ user_aliases: ['My Saga'] }));
    const path = `${RECORD}/preferred-language`;
    page.route(`PUT ${path}`, call => metadataRecord({
        preferred_language: call.body.language,
        user_aliases: ['My Saga', 'Saga (KR)']
    }));
    const refreshes = libraryRefreshes(page);

    page.input(languageSelect(page), 'ko');
    await page.settle();

    const [first] = callsTo(page, path, 'PUT');
    assert.deepEqual(first.body, { language: 'ko' });
    assert.equal(first.headers['content-type'], 'application/json');
    assert.deepEqual(lastMessage(page), { text: 'Preferred language set to ko', type: 'success' });
    assert.equal(libraryRefreshes(page), refreshes + 1, 'the library shows the new display names');

    page.input(languageSelect(page), '');
    await page.settle();

    assert.deepEqual(callsTo(page, path, 'PUT')[1].body, { language: null });
    assert.deepEqual(lastMessage(page), { text: 'Preferred language cleared', type: 'success' });
    assert.equal(libraryRefreshes(page), refreshes + 2);

    // The modal keeps working with the record the server sent back.
    page.route(`PUT ${RECORD}/aliases`, call => metadataRecord({ user_aliases: call.body.aliases }));
    await press(page, 'Save');
    assert.deepEqual(callsTo(page, `${RECORD}/aliases`, 'PUT')[0].body, { aliases: ['My Saga', 'Saga (KR)'] });
});

test('preferred language errors show the server message when it sends one', async () => {
    const page = await bootSeries();
    await openManageNames(page);
    const path = `${RECORD}/preferred-language`;
    const refreshes = libraryRefreshes(page);
    const cases = [
        [respond(400, { error: 'Unsupported language "xx"' }), 'Unsupported language "xx"'],
        [respond(400, '"Language must be a two-letter code"', { 'Content-Type': 'application/json' }), 'Language must be a two-letter code'],
        [respond(400, {}), 'Failed to update preferred language'],
        [respond(400, null), 'Failed to update preferred language'],
        [respond(400, 'Bad Request'), 'Failed to update preferred language'],
        [offline, 'Failed to update preferred language']
    ];

    for (const [handler, expected] of cases) {
        page.route(`PUT ${path}`, handler);
        page.input(languageSelect(page), 'en');
        await page.settle();
        assert.deepEqual(lastMessage(page), { text: expected, type: 'error' });
    }

    assert.equal(callsTo(page, path, 'PUT').length, cases.length);
    assert.ok(logged(page, 'saveManageSeriesPreferredLanguage failed'));
    assert.equal(libraryRefreshes(page), refreshes, 'nothing changed, so the library is not reloaded');
});

test('picking a series name saves it and rebuilds the list from the response', async () => {
    const page = await bootSeries();
    await openManageNames(page, RICH);
    const path = `${RECORD}/name`;
    page.route(`PUT ${path}`, call => ({
        ...RICH,
        series_name: call.body.name,
        resolved_series_name: call.body.name || 'サーガ',
        user_aliases: ['My Saga', 'Saga Reloaded']
    }));
    const refreshes = libraryRefreshes(page);

    page.input(nameSelect(page), 'Saga Comic');
    await page.settle();

    const [put] = callsTo(page, path, 'PUT');
    assert.deepEqual(put.body, { name: 'Saga Comic' });
    assert.equal(put.headers['content-type'], 'application/json');
    assert.deepEqual(lastMessage(page), { text: 'Series name set to "Saga Comic"', type: 'success' });
    assert.equal(nameSelect(page).value, 'Saga Comic');
    assert.equal(text(page.$('#manageSeriesResolvedName')), 'Using your selected name: "Saga Comic".');
    assert.deepEqual(userAliases(page), ['My Saga', 'Saga Reloaded']);
    assert.equal(optionTexts(nameSelect(page)).at(-1), 'Saga Reloaded (your alias)');
    assert.equal(libraryRefreshes(page), refreshes + 1);

    page.input(nameSelect(page), '');
    await page.settle();

    assert.deepEqual(callsTo(page, path, 'PUT')[1].body, { name: null });
    assert.deepEqual(lastMessage(page), { text: 'Series name set to automatic', type: 'success' });
    assert.equal(nameSelect(page).value, '');
    assert.equal(text(page.$('#manageSeriesResolvedName')), 'Automatic name currently resolves to: "サーガ".');
    assert.equal(libraryRefreshes(page), refreshes + 2);
});

test('a rejected series name is reported and the previous choice is restored', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ aliases: ['Saga Comic'], user_aliases: ['My Saga'] }));
    const path = `${RECORD}/name`;
    const refreshes = libraryRefreshes(page);

    page.route(`PUT ${path}`, respond(400, { error: 'Name is not known for this series' }));
    page.input(nameSelect(page), 'Saga Comic');
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Name is not known for this series', type: 'error' });
    assert.equal(nameSelect(page).value, '', 'back to Automatic');

    page.route(`PUT ${path}`, call => metadataRecord({
        aliases: ['Saga Comic'],
        user_aliases: ['My Saga'],
        series_name: call.body.name,
        resolved_series_name: call.body.name
    }));
    page.input(nameSelect(page), 'Saga Comic');
    await page.settle();
    assert.equal(nameSelect(page).value, 'Saga Comic');

    const failures = [
        [respond(409, '"Name already in use"', { 'Content-Type': 'application/json' }), 'Name already in use'],
        [respond(400, 'Bad Request'), 'Failed to update series name'],
        [respond(500), 'Failed to update series name']
    ];
    for (const [handler, expected] of failures) {
        page.route(`PUT ${path}`, handler);
        page.input(nameSelect(page), 'My Saga');
        await page.settle();
        assert.deepEqual(lastMessage(page), { text: expected, type: 'error' });
        assert.equal(nameSelect(page).value, 'Saga Comic', 'the stored name is selected again');
    }

    page.route(`PUT ${path}`, offline);
    page.input(nameSelect(page), 'My Saga');
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Failed to update series name', type: 'error' });
    assert.ok(logged(page, 'saveManageSeriesName failed'));
    assert.equal(libraryRefreshes(page), refreshes + 1, 'only the accepted name reloads the library');
});

test('user aliases can be added and removed before saving them', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ aliases: ['Saga Comic'], user_aliases: ['My Saga'] }));
    const input = page.$('#manageSeriesNewAlias');

    addAlias(page, '  Saga Reborn  ');
    assert.deepEqual(userAliases(page), ['My Saga', 'Saga Reborn'], 'the alias is trimmed');
    assert.equal(input.value, '');
    assert.deepEqual(optionTexts(nameSelect(page)).slice(-2), ['My Saga (your alias)', 'Saga Reborn (your alias)'],
        'a new alias can be picked as the series name right away');

    addAlias(page, 'my saga');
    assert.deepEqual(userAliases(page), ['My Saga', 'Saga Reborn'], 'aliases are case-insensitively unique');
    assert.equal(input.value, '');

    addAlias(page, '   ');
    assert.deepEqual(userAliases(page), ['My Saga', 'Saga Reborn']);
    addAlias(page, '');
    assert.deepEqual(userAliases(page), ['My Saga', 'Saga Reborn'], 'an empty Add does nothing');

    const quoted = 'Tom\'s "Saga" \\ Vol';
    addAlias(page, quoted);
    assert.deepEqual(userAliases(page), ['My Saga', 'Saga Reborn', quoted]);
    page.click(removeButton(page, quoted));
    assert.deepEqual(userAliases(page), ['My Saga', 'Saga Reborn'], 'quotes and backslashes survive the remove button');

    page.click(removeButton(page, 'My Saga'));
    assert.deepEqual(userAliases(page), ['Saga Reborn']);
    assert.deepEqual(optionValues(nameSelect(page)), ['', 'Saga', 'Saga Comic', 'Saga Reborn']);
    assert.equal(callsTo(page, `${RECORD}/aliases`).length, 0, 'nothing is saved until Save');

    page.click(removeButton(page, 'Saga Reborn'));
    assert.equal(text(page.$('#manageSeriesUserAliases')), 'None');
    addAlias(page, 'Saga Reborn');

    page.route(`PUT ${RECORD}/aliases`, call => metadataRecord({ user_aliases: call.body.aliases }));
    const refreshes = libraryRefreshes(page);
    const healthChecks = callsTo(page, '/api/files/counts', 'GET').length;
    await press(page, 'Save');

    const [put] = callsTo(page, `${RECORD}/aliases`, 'PUT');
    assert.deepEqual(put.body, { aliases: ['Saga Reborn'] });
    assert.equal(put.headers['content-type'], 'application/json');
    assert.deepEqual(lastMessage(page), { text: 'Series names saved', type: 'success' });
    assert.equal(modalOpen(page), false);
    assert.equal(libraryRefreshes(page), refreshes + 1);
    assert.equal(callsTo(page, '/api/files/counts', 'GET').length, healthChecks + 2,
        'the alias save and the library reload each refresh the combinable-folder count');
});

test('a series without user aliases saves an empty list and starts a new one on Add', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ user_aliases: null }));
    page.route(`PUT ${RECORD}/aliases`, call => metadataRecord({ user_aliases: call.body.aliases }));

    await press(page, 'Save');
    assert.deepEqual(callsTo(page, `${RECORD}/aliases`, 'PUT')[0].body, { aliases: [] });

    await openManageNames(page, metadataRecord({ user_aliases: null }));
    addAlias(page, 'Saga Reborn');
    assert.deepEqual(userAliases(page), ['Saga Reborn']);
    await press(page, 'Save');
    assert.deepEqual(callsTo(page, `${RECORD}/aliases`, 'PUT')[1].body, { aliases: ['Saga Reborn'] });

    // No × buttons are rendered without aliases; a direct call has nothing to remove.
    await openManageNames(page, metadataRecord({ user_aliases: null }));
    page.run('removeManageSeriesAlias(\'Saga\')');
    assert.equal(text(page.$('#manageSeriesUserAliases')), 'None');
});

test('an alias typed after a failed load is listed as a selectable name', async () => {
    const page = await bootSeries();
    await openManageNames(page, respond(503));

    addAlias(page, 'Saga Reborn');

    assert.deepEqual(userAliases(page), ['Saga Reborn']);
    assert.deepEqual(optionTexts(nameSelect(page)), [AUTOMATIC, 'Saga Reborn (your alias)']);
});

test('aliases typed after a failed load join the stored ones a later response brings', async () => {
    const page = await bootSeries();
    await openManageNames(page, respond(503));
    page.route('POST /api/metadata/refresh/Saga', metadataRecord({ user_aliases: ['My Saga'], lookup_status: 'success' }));

    addAlias(page, 'Saga Reborn');
    await press(page, 'Refresh from Provider');

    assert.deepEqual(userAliases(page), ['My Saga', 'Saga Reborn']);
    page.click(removeButton(page, 'My Saga'));
    page.route(`PUT ${RECORD}/aliases`, call => metadataRecord({ user_aliases: call.body.aliases }));
    await press(page, 'Save');
    assert.deepEqual(callsTo(page, `${RECORD}/aliases`, 'PUT')[0].body, { aliases: ['Saga Reborn'] },
        'once listed, a stored alias can be removed');
});

test('failing to save the aliases keeps the modal open', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ user_aliases: ['My Saga'] }));
    const refreshes = libraryRefreshes(page);

    page.route(`PUT ${RECORD}/aliases`, respond(500, 'Error updating aliases'));
    await press(page, 'Save');
    assert.deepEqual(lastMessage(page), { text: 'Failed to save series names', type: 'error' });
    assert.ok(modalOpen(page));

    page.route(`PUT ${RECORD}/aliases`, offline);
    await press(page, 'Save');
    assert.deepEqual(lastMessage(page), { text: 'Failed to save series names', type: 'error' });
    assert.ok(modalOpen(page));
    assert.ok(logged(page, 'saveManageSeriesNames failed'));
    assert.equal(libraryRefreshes(page), refreshes);
});

test('unsaved alias edits survive a preferred-language change and are what Save persists (#848)', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ user_aliases: ['My Saga', 'Old Saga'] }));
    // The server answers with the stored aliases: it has not seen the edits.
    page.route(`PUT ${RECORD}/preferred-language`, call => metadataRecord({
        preferred_language: call.body.language,
        user_aliases: ['My Saga', 'Old Saga']
    }));

    addAlias(page, 'New Saga');
    page.click(removeButton(page, 'Old Saga'));
    page.input(languageSelect(page), 'ko');
    await page.settle();

    assert.deepEqual(lastMessage(page), { text: 'Preferred language set to ko', type: 'success' });
    assert.deepEqual(userAliases(page), ['My Saga', 'New Saga']);

    page.route(`PUT ${RECORD}/aliases`, call => metadataRecord({ user_aliases: call.body.aliases }));
    await press(page, 'Save');
    assert.deepEqual(callsTo(page, `${RECORD}/aliases`, 'PUT')[0].body, { aliases: ['My Saga', 'New Saga'] });
    assert.deepEqual(lastMessage(page), { text: 'Series names saved', type: 'success' });
});

test('picking a name, refreshing or clearing external metadata keeps unsaved alias edits (#848)', async () => {
    const page = await bootSeries();
    const stored = { aliases: ['Saga Comic'], user_aliases: ['My Saga', 'Old Saga'] };
    await openManageNames(page, metadataRecord(stored));
    page.route(`PUT ${RECORD}/name`, call => metadataRecord({
        ...stored,
        series_name: call.body.name,
        resolved_series_name: call.body.name
    }));
    page.route('POST /api/metadata/refresh/Saga', metadataRecord({ ...stored, lookup_status: 'success' }));
    page.route(`DELETE ${RECORD}/external`, metadataRecord({ user_aliases: stored.user_aliases }));
    answerConfirm(page, true);
    const edited = ['My Saga', 'New Saga'];

    addAlias(page, 'New Saga');
    page.click(removeButton(page, 'Old Saga'));

    page.input(nameSelect(page), 'Saga Comic');
    await page.settle();
    assert.deepEqual(lastMessage(page), { text: 'Series name set to "Saga Comic"', type: 'success' });
    assert.deepEqual(userAliases(page), edited, 'after picking a name');

    await press(page, 'Refresh from Provider');
    assert.deepEqual(lastMessage(page), { text: 'Metadata refreshed', type: 'success' });
    assert.deepEqual(userAliases(page), edited, 'after a refresh');

    await press(page, 'Clear External Metadata');
    assert.deepEqual(lastMessage(page), { text: 'External metadata cleared', type: 'success' });
    assert.deepEqual(userAliases(page), edited, 'after clearing the external metadata');
    assert.deepEqual(optionValues(nameSelect(page)), ['', 'Saga', ...edited]);

    page.route(`PUT ${RECORD}/aliases`, call => metadataRecord({ user_aliases: call.body.aliases }));
    await press(page, 'Save');
    assert.deepEqual(callsTo(page, `${RECORD}/aliases`, 'PUT')[0].body, { aliases: edited });
});

test('Refresh from Provider reloads the provider names, image and lookup status', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({ user_aliases: ['My Saga'] }));
    const path = '/api/metadata/refresh/Saga';
    page.route('GET /api/series-images/saga', respond(200, 'PNGDATA', { 'Content-Type': 'image/png' }));
    page.route(`POST ${path}`, metadataRecord({
        aliases: ['Saga Comic'],
        user_aliases: ['My Saga'],
        source: 'ComicVine',
        last_lookup_utc: '2024-06-01T08:00:00Z',
        lookup_status: 'success',
        has_image: true,
        image_downloaded_utc: '2024-06-01T08:00:05Z'
    }));

    await press(page, 'Refresh from Provider');

    const [post] = callsTo(page, path, 'POST');
    assert.deepEqual(Object.keys(post.query), [], 'a synchronous lookup, not a queued one');
    assert.deepEqual(lastMessage(page), { text: 'Metadata refreshed', type: 'success' });
    assert.deepEqual(providerAliases(page), ['Saga Comic']);
    assert.equal(
        text(page.$('#manageSeriesProviderSource')),
        `Source: ComicVine · Last lookup: ${localTime(page, '2024-06-01T08:00:00Z')} · Status: success`
    );
    assert.deepEqual(userAliases(page), ['My Saga']);
    assert.deepEqual(optionValues(nameSelect(page)), ['', 'Saga', 'Saga Comic', 'My Saga']);
    assert.equal(
        text(page.$('#manageSeriesImageStatus')),
        `Downloaded from provider · ${localTime(page, '2024-06-01T08:00:05Z')}`
    );
    const preview = page.$('#manageSeriesImagePreview');
    assert.ok(page.objectUrls.has(preview.src), 'the downloaded image is previewed');

    const outcomes = [
        [metadataRecord({ lookup_status: 'manual_match' }), { text: 'Metadata refreshed', type: 'success' }],
        [metadataRecord({ lookup_status: 'not_found' }), { text: 'Lookup status: not_found', type: 'info' }],
        [metadataRecord({ lookup_status: null }), { text: 'Lookup status: unknown', type: 'info' }],
        [respond(502, { error: 'ComicVine is unavailable' }), { text: 'Failed to refresh metadata', type: 'error' }],
        [offline, { text: 'Failed to refresh metadata', type: 'error' }]
    ];
    for (const [handler, expected] of outcomes) {
        page.route(`POST ${path}`, handler);
        await press(page, 'Refresh from Provider');
        assert.deepEqual(lastMessage(page), expected);
    }
    assert.ok(logged(page, 'refreshSeriesMetadata failed'));
    assert.equal(callsTo(page, path, 'POST').length, 1 + outcomes.length);
});

test('Clear External Metadata asks first and then drops the provider names', async () => {
    const page = await bootSeries();
    await openManageNames(page, metadataRecord({
        aliases: ['Saga Comic'],
        user_aliases: ['My Saga'],
        source: 'ComicVine',
        lookup_status: 'success'
    }));
    const path = `${RECORD}/external`;

    answerConfirm(page, false);
    await press(page, 'Clear External Metadata');
    assert.deepEqual(page.dialogs.map(dialog => [dialog.kind, dialog.message]), [['confirm', CLEAR_PROMPT]]);
    assert.equal(callsTo(page, path).length, 0);
    assert.deepEqual(providerAliases(page), ['Saga Comic']);

    answerConfirm(page, true);
    page.route(`DELETE ${path}`, metadataRecord({ user_aliases: ['My Saga'] }));
    const refreshes = libraryRefreshes(page);
    await press(page, 'Clear External Metadata');

    assert.equal(callsTo(page, path, 'DELETE').length, 1);
    assert.deepEqual(lastMessage(page), { text: 'External metadata cleared', type: 'success' });
    assert.equal(text(page.$('#manageSeriesProviderAliases')), 'None');
    assert.equal(text(page.$('#manageSeriesProviderSource')), NO_LOOKUP);
    assert.deepEqual(userAliases(page), ['My Saga'], 'user aliases are kept');
    assert.deepEqual(optionValues(nameSelect(page)), ['', 'Saga', 'My Saga']);
    assert.equal(libraryRefreshes(page), refreshes + 1);

    const outcomes = [
        [404, { text: 'Nothing to clear — no external metadata cached for this series.', type: 'info' }],
        [respond(500, 'Error clearing metadata'), { text: 'Failed to clear external metadata', type: 'error' }],
        [offline, { text: 'Failed to clear external metadata', type: 'error' }]
    ];
    for (const [handler, expected] of outcomes) {
        page.route(`DELETE ${path}`, handler);
        await press(page, 'Clear External Metadata');
        assert.deepEqual(lastMessage(page), expected);
    }
    assert.ok(logged(page, 'clearSeriesExternalMetadata failed'));
    assert.equal(libraryRefreshes(page), refreshes + 1);
});

test('Check for Combinable Folders swaps the modal for Combine Folders', async () => {
    const page = await bootSeries({ routes: { 'GET /api/files/combinable-folders': { groups: [] } } });
    await openManageNames(page);
    const healthChecks = callsTo(page, '/api/files/counts', 'GET').length;

    await press(page, 'Check for Combinable Folders');

    assert.ok(messages(page).some(message =>
        message.text === 'Checking for combinable folders...' && message.type === 'info'));
    assert.equal(callsTo(page, '/api/files/counts', 'GET').length, healthChecks + 1);
    assert.equal(modalOpen(page), false);
    assert.ok(page.$('#combineFoldersModal').classList.contains('active'));
    assert.equal(callsTo(page, '/api/files/combinable-folders', 'GET').length, 1);
    assert.equal(page.$('#combineFoldersEmptyState').style.display, 'block');
});

test('a failure while opening Combine Folders is reported', async () => {
    const page = await bootSeries();
    await openManageNames(page);
    page.window.openCombineFoldersModal = () => Promise.reject(new Error('combine folders unavailable'));

    await press(page, 'Check for Combinable Folders');

    assert.deepEqual(lastMessage(page), { text: 'Failed to check for combinable folders', type: 'error' });
    assert.ok(logged(page, 'checkCombinableFoldersFromManageSeries failed'));
    assert.equal(modalOpen(page), false);
});

test('read-only users are offered none of the controls that change the series', async () => {
    // The buttons a user is shown once the modal lists a user alias and a
    // provider search result, and whether they can type a new alias.
    const shown = async user => {
        const page = await bootSeries({ user, beforeScripts: addReadOnlyGate });
        page.route(`GET ${RECORD}`, metadataRecord({ user_aliases: ['My Saga'] }));
        page.route('GET /api/metadata/search', { results: [{ canonical_title: 'Saga', source: 'ComicVine', aliases: [] }] });
        // Read-only users don't get the Actions menu that opens it.
        await page.run("openManageSeriesNamesModal('Saga')");
        await page.settle();
        page.click(`${MODAL} button[onclick="searchExternalSeries()"]`);
        await page.settle();
        return {
            buttons: page.$$(`${MODAL} button`).filter(isDisplayed).map(text),
            newAlias: isDisplayed(page.$('#manageSeriesNewAlias'))
        };
    };

    assert.deepEqual(await shown({ canModifyLibrary: false, canAdminister: false, roles: ['ReadOnly'] }), {
        buttons: ['×', 'Search', 'Close'],
        newAlias: false
    });
    assert.deepEqual(await shown({}), {
        buttons: [
            '×', '×', 'Add', 'Upload Image', 'Fetch from Provider', 'Clear Image', 'Search',
            'Use This Match', 'Adopt as Canonical', 'Add Aliases',
            'Refresh from Provider', 'Clear External Metadata', 'Check for Combinable Folders', 'Save', 'Close'
        ],
        newAlias: true
    });
});
