'use strict';

// Regression tests for js/app/series-metadata.js: every /api/metadata request
// carries the session token (MetadataController is [Authorize] and reads the
// JWT from the Authorization header).

const test = require('node:test');
const assert = require('node:assert/strict');
const { bootSeries, metadataRecord, callsTo, text, openManageNames, buttonIn } = require('./support/series-helpers');

const MODAL = '#manageSeriesNamesModal';
const RECORD = '/api/metadata/series/Saga';

const bearer = page => `Bearer ${page.window.localStorage.getItem('jwt_token')}`;

function menuItem(page, label) {
    const item = page.$$('#metadataDropdown .action-dropdown-item').find(el => text(el) === label);
    if (!item) throw new Error(`No Metadata menu item "${label}"`);
    return item;
}

async function clickAndSettle(page, element) {
    page.click(element);
    await page.settle();
}

async function changeAndSettle(page, selector, value) {
    page.input(selector, value);
    await page.settle();
}

function assertAuthorized(page, method, path) {
    const calls = callsTo(page, path, method);
    assert.equal(calls.length, 1, `one ${method} ${path}`);
    const [call] = calls;
    assert.equal(call.headers.authorization, bearer(page), `${method} ${path} sends the session token`);
    assert.equal(call.init.credentials, 'same-origin', `${method} ${path} keeps its credentials mode`);
    return call;
}

test('Refresh All External Metadata and Match All Unmatched Series send the session token', async () => {
    const page = await bootSeries({
        hash: '#/library',
        routes: {
            'POST /api/metadata/refresh-all': { totalSeries: 0 },
            'POST /api/metadata/match-unmatched': { totalSeries: 0 }
        }
    });

    await clickAndSettle(page, menuItem(page, 'Refresh All External Metadata'));
    await clickAndSettle(page, menuItem(page, 'Match All Unmatched Series'));

    for (const path of ['/api/metadata/refresh-all', '/api/metadata/match-unmatched']) {
        const post = assertAuthorized(page, 'POST', path);
        assert.equal(post.headers['content-type'], 'application/json', path);
    }
});

test('every Manage Names request to the metadata API sends the session token', async () => {
    const record = metadataRecord({ aliases: ['Saga Comic'], user_aliases: ['My Saga'] });
    const page = await bootSeries({
        routes: {
            [`PUT ${RECORD}/preferred-language`]: call => ({ ...record, preferred_language: call.body.language }),
            [`PUT ${RECORD}/name`]: call => ({ ...record, series_name: call.body.name }),
            'POST /api/metadata/refresh/Saga': { ...record, lookup_status: 'success' },
            'GET /api/metadata/search': { results: [{ canonical_title: 'Saga (Image Comics)', source: 'ComicVine', aliases: [] }] },
            [`POST ${RECORD}/apply-match`]: { ...record, lookup_status: 'manual_match' },
            [`DELETE ${RECORD}/external`]: { ...record, aliases: [] },
            [`PUT ${RECORD}/aliases`]: call => ({ ...record, user_aliases: call.body.aliases })
        }
    });

    await openManageNames(page, record);
    await changeAndSettle(page, '#manageSeriesPreferredLanguage', 'ko');
    await changeAndSettle(page, '#manageSeriesName', 'Saga Comic');
    await clickAndSettle(page, buttonIn(page, `${MODAL} .modal-footer`, 'Refresh from Provider'));
    await clickAndSettle(page, page.$(`${MODAL} button[onclick="searchExternalSeries()"]`));
    await clickAndSettle(page, buttonIn(page, '#manageSeriesSearchResults', 'Use This Match'));
    await clickAndSettle(page, buttonIn(page, `${MODAL} .modal-footer`, 'Clear External Metadata'));
    await clickAndSettle(page, buttonIn(page, `${MODAL} .modal-footer`, 'Save'));

    assertAuthorized(page, 'GET', RECORD);
    assertAuthorized(page, 'POST', '/api/metadata/refresh/Saga');
    assertAuthorized(page, 'DELETE', `${RECORD}/external`);
    const search = assertAuthorized(page, 'GET', '/api/metadata/search');
    assert.deepEqual({ ...search.query }, { query: 'Saga', limit: '10' });

    // The JSON requests keep their method, content type and body.
    const bodies = [
        ['PUT', `${RECORD}/preferred-language`, { language: 'ko' }],
        ['PUT', `${RECORD}/name`, { name: 'Saga Comic' }],
        ['PUT', `${RECORD}/aliases`, { aliases: ['My Saga'] }]
    ];
    for (const [method, path, body] of bodies) {
        const call = assertAuthorized(page, method, path);
        assert.equal(call.headers['content-type'], 'application/json', path);
        assert.deepEqual(call.body, body, path);
    }
    const match = assertAuthorized(page, 'POST', `${RECORD}/apply-match`);
    assert.equal(match.headers['content-type'], 'application/json');
    assert.equal(match.body.canonicalTitle, 'Saga (Image Comics)');
    assert.equal(match.body.source, 'ComicVine');
    const unanswered = page.unmatchedRequests.filter(call => call.path.startsWith('/api/metadata'));
    assert.deepEqual(unanswered.map(call => `${call.method} ${call.path}`), []);
});
