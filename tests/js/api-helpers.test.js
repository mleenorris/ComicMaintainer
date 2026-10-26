'use strict';

// js/app/api.js — showMessage(), API failure references, preferences,
// fetch options, file-path encoding and JWT decoding.

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage, plain, respond } = require('./support/dom');
const { jwt, validToken } = require('./support/app');
const { loadScripts } = require('./support/sandbox');
const { consoleLines, messages, wentToLogin } = require('./support/core-helpers');

function apiPage(options = {}) {
    return loadPage({
        scripts: ['js/shared.js', 'js/app/api.js'],
        localStorage: { jwt_token: validToken(), username: 'alice' },
        ...options
    });
}

test('showMessage shows an info message in the polite status region for five seconds', t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = apiPage();
    assert.equal(page.$('#messageContainer').getAttribute('aria-live'), 'polite');
    const element = page.run("showMessage('Saved settings')");
    assert.deepEqual(messages(page), [{ type: 'info', text: 'Saved settings', role: null }]);
    t.mock.timers.tick(4999);
    assert.equal(messages(page).length, 1);
    t.mock.timers.tick(1);
    assert.deepEqual(messages(page), []);
    assert.equal(element.isConnected, false);
});

test('showMessage announces errors as alerts and honours a custom duration', t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = apiPage();
    page.run("showMessage('Could not save', 'error', { duration: 1000 })");
    page.run("showMessage('Copied', 'success')");
    assert.deepEqual(messages(page), [
        { type: 'error', text: 'Could not save', role: 'alert' },
        { type: 'success', text: 'Copied', role: null }
    ]);
    t.mock.timers.tick(1000);
    assert.deepEqual(messages(page).map(message => message.text), ['Copied']);
    t.mock.timers.tick(4000);
    assert.deepEqual(messages(page), []);
});

test('showMessage keeps persistent messages until the caller removes them', t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = apiPage();
    const element = page.run("showMessage('Exporting…', 'info', { autoDismiss: false })");
    t.mock.timers.tick(60000);
    assert.deepEqual(messages(page).map(message => message.text), ['Exporting…']);
    element.textContent = 'Exported 3 files';
    element.remove();
    assert.deepEqual(messages(page), []);
});

test('a failed API call\'s correlation id is appended to the error reported for it', async () => {
    const page = apiPage({
        routes: {
            'GET /api/files/series': respond(500, { error: 'database is locked' }, { 'X-Correlation-Id': 'corr-500' })
        }
    });
    const response = await page.run("fetch(apiUrl('/api/files/series'))");
    assert.equal(response.status, 500, 'the wrapped fetch still returns the response');
    page.run("showMessage('Failed to load series', 'error', { reference: true })");
    page.run("showMessage('Failed to load series (ref: corr-500)', 'error', { reference: true })");
    page.run("showMessage('Clipboard access denied', 'error')");
    assert.deepEqual(messages(page).map(message => message.text), [
        'Failed to load series (ref: corr-500)',
        'Failed to load series (ref: corr-500)',
        'Clipboard access denied'
    ]);
});

test('only non-auth failures that carry a correlation id are remembered', async () => {
    const page = apiPage({
        routes: {
            'GET /api/first': respond(500, { error: 'boom' }, { 'X-Correlation-Id': 'corr-first' }),
            'GET /api/unauthorized': respond(401, { error: 'expired' }, { 'X-Correlation-Id': 'corr-401' }),
            'GET /api/forbidden': respond(403, { error: 'read-only' }, { 'X-Correlation-Id': 'corr-403' }),
            'GET /api/no-id': respond(502, 'Bad gateway'),
            'GET /api/ok': respond(200, { ok: true }, { 'X-Correlation-Id': 'corr-ok' })
        }
    });
    assert.equal(page.run('withApiFailureReference("Nothing failed", true)'), 'Nothing failed');
    await page.run("fetch('/api/first')");
    for (const path of ['/api/unauthorized', '/api/forbidden', '/api/no-id', '/api/ok']) {
        await page.run(`fetch('${path}')`);
    }
    assert.equal(page.run('withApiFailureReference("Failed", true)'), 'Failed (ref: corr-first)');
    assert.equal(page.run('withApiFailureReference("Failed", "explicit-7")'), 'Failed (ref: explicit-7)');
    assert.equal(page.run('withApiFailureReference("Failed", "")'), 'Failed');
});

test('a correlation id older than fifteen seconds is no longer attached', async () => {
    const page = apiPage({
        routes: { 'POST /api/jobs': respond(500, { error: 'boom' }, { 'X-Correlation-Id': 'corr-old' }) }
    });
    page.run('Date.now = () => 1700000000000');
    await page.run("fetch('/api/jobs', { method: 'POST' })");
    page.run('Date.now = () => 1700000015000');
    assert.equal(page.run('withApiFailureReference("Job failed", true)'), 'Job failed (ref: corr-old)');
    page.run('Date.now = () => 1700000015001');
    assert.equal(page.run('withApiFailureReference("Job failed", true)'), 'Job failed');
});

test('opaque failed responses whose headers cannot be read are ignored', async () => {
    const page = apiPage({
        routes: {
            'GET /api/opaque'() {
                const response = new Response(null, { status: 500 });
                Object.defineProperty(response, 'headers', {
                    get() { throw new TypeError('Headers of an opaque response are not accessible'); }
                });
                return response;
            }
        }
    });
    const response = await page.run("fetch('/api/opaque')");
    assert.equal(response.status, 500);
    assert.equal(page.run('withApiFailureReference("Failed", true)'), 'Failed');
});

test('api.js leaves fetch alone when the browser has none to wrap', () => {
    const sandbox = loadScripts(['js/app/api.js'], { fetch: undefined });
    assert.equal(sandbox.run('typeof window.fetch'), 'undefined');
    assert.equal(sandbox.run('withApiFailureReference("Failed", true)'), 'Failed');
});

test('getPreferences returns the saved preferences, or {} when they cannot be loaded', async () => {
    const prefs = { theme: 'dark', perPage: 50, libraryViewMode: 'series', filterMode: 'unmarked', sortMode: 'date' };
    const ok = apiPage({ routes: { 'GET /api/preferences': prefs } });
    assert.deepEqual(plain(await ok.run('getPreferences()')), prefs);
    const call = ok.fetchCalls.find(entry => entry.path === '/api/preferences');
    assert.match(call.headers.authorization, /^Bearer /);

    const failing = apiPage({ routes: { 'GET /api/preferences': 500 } });
    assert.deepEqual(Object.keys(await failing.run('getPreferences()')), []);
    assert.ok(consoleLines(failing, 'error').includes('Failed to get preferences: 500'));
    assert.equal(wentToLogin(failing), false);

    const offline = apiPage({ routes: { 'GET /api/preferences': () => { throw new TypeError('Failed to fetch'); } } });
    assert.deepEqual(Object.keys(await offline.run('getPreferences()')), []);
    assert.ok(consoleLines(offline, 'error').some(line => line.startsWith('Error getting preferences:')));

    const expired = apiPage({ routes: { 'GET /api/preferences': 401 } });
    assert.deepEqual(Object.keys(await expired.run('getPreferences()')), []);
    assert.ok(wentToLogin(expired));
});

test('setPreferences PUTs the changed preferences as JSON', async () => {
    const page = apiPage({ routes: { 'PUT /api/preferences': { theme: 'dark' } } });
    await page.run("setPreferences({ theme: 'dark', perPage: 25 })");
    const [call] = page.fetchCalls;
    assert.equal(call.method, 'PUT');
    assert.equal(call.path, '/api/preferences');
    assert.deepEqual(call.body, { theme: 'dark', perPage: 25 });
    assert.equal(call.headers['content-type'], 'application/json');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.deepEqual(consoleLines(page, 'error'), []);
});

test('setPreferences reports rejected and failed saves without throwing', async () => {
    const failing = apiPage({ routes: { 'PUT /api/preferences': 400 } });
    assert.equal(await failing.run("setPreferences({ perPage: 7 })"), undefined);
    assert.ok(consoleLines(failing, 'error').includes('Failed to set preferences: 400'));

    const offline = apiPage({ routes: { 'PUT /api/preferences': () => { throw new TypeError('Failed to fetch'); } } });
    await offline.run("setPreferences({ perPage: 7 })");
    assert.ok(consoleLines(offline, 'error').some(line => line.startsWith('Error setting preferences:')));

    const expired = apiPage({ routes: { 'PUT /api/preferences': 401 } });
    await expired.run("setPreferences({ perPage: 7 })");
    assert.ok(wentToLogin(expired));
    assert.deepEqual(consoleLines(expired, 'error'), []);
});

test('getFetchOptions sends credentials and auth headers and serialises object bodies', () => {
    const token = validToken();
    const page = apiPage({ localStorage: { jwt_token: token } });
    const get = page.evaluate('getFetchOptions()');
    assert.equal(get.method, 'GET');
    assert.equal(get.credentials, 'include');
    assert.equal(get.headers.Authorization, `Bearer ${token}`);
    assert.equal('body' in get, false);
    assert.equal(page.evaluate("getFetchOptions('POST', { files: ['a.cbz'] })").body, '{"files":["a.cbz"]}');
    assert.equal(page.evaluate("getFetchOptions('PUT', 'raw text')").body, 'raw text');
});

test('encodeFilePathForUrl produces unpadded base64url of the UTF-8 path', () => {
    const page = apiPage();
    for (const filePath of ['Saga/Saga 001.cbz', '>>>???', 'Ästhetik/漫画 #1 (2024).cbz', 'a']) {
        const encoded = page.run(`encodeFilePathForUrl(${JSON.stringify(filePath)})`);
        assert.equal(encoded, Buffer.from(filePath, 'utf8').toString('base64url'), filePath);
        assert.doesNotMatch(encoded, /[+/=]/);
        assert.equal(Buffer.from(encoded, 'base64url').toString('utf8'), filePath);
    }
});

test('decodeJwtToken reads UTF-8 payloads and rejects malformed tokens', () => {
    const page = apiPage();
    const token = jwt({ sub: 'zoë', name: '漫画', exp: 1700000000 });
    assert.deepEqual(page.evaluate(`decodeJwtToken(${JSON.stringify(token)})`), { sub: 'zoë', name: '漫画', exp: 1700000000 });
    assert.equal(page.run("decodeJwtToken('not-a-jwt')"), null);
    assert.deepEqual(consoleLines(page, 'error'), []);
    assert.equal(page.run("decodeJwtToken('a.!!!.c')"), null);
    assert.ok(consoleLines(page, 'error').some(line => line.startsWith('Error decoding JWT token:')));
});

test('isTokenExpired treats tokens without a readable exp as expired', () => {
    const page = apiPage();
    page.run('Date.now = () => 1700000000000');
    const expired = value => page.run(`isTokenExpired(${JSON.stringify(value)})`);
    assert.equal(expired(jwt({ sub: 'alice', exp: 1700000001 })), false);
    assert.equal(expired(jwt({ sub: 'alice', exp: 1700000000 })), true, 'expires at exactly exp');
    assert.equal(expired(jwt({ sub: 'alice' })), true, 'no exp claim');
    assert.equal(expired('garbage'), true);
});
