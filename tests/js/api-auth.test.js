'use strict';

// js/app/api.js — session checks (checkAuth), auth headers, the login
// redirect and sign-out.

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage, plain } = require('./support/dom');
const { jwt, validToken } = require('./support/app');
const { boot, consoleLines, requests, wentToLogin } = require('./support/core-helpers');

const expiredToken = () => jwt({ sub: 'alice', exp: Math.floor(Date.now() / 1000) - 60 });

// index.html's markup and inline scripts with only the API plumbing loaded:
// enough to call the helpers directly without booting the whole app.
function apiPage(options = {}) {
    return loadPage({ scripts: ['js/shared.js', 'js/app/api.js'], ...options });
}

const AUTH_STATUS = {
    'answers (JWT mode)': { autheliaEnabled: false, isAuthenticated: false },
    'fails (500)': 500,
    'is unreachable': () => { throw new TypeError('Failed to fetch'); }
};

for (const [description, status] of Object.entries(AUTH_STATUS)) {
    test(`checkAuth when /api/auth/status ${description}: the stored token decides`, async () => {
        const cases = [
            { name: 'no token', storage: {}, expected: false },
            { name: 'expired token', storage: { jwt_token: expiredToken(), username: 'alice' }, expected: false },
            { name: 'valid token', storage: { jwt_token: validToken(), username: 'alice' }, expected: true }
        ];
        for (const { name, storage, expected } of cases) {
            const page = apiPage({ localStorage: storage, routes: { 'GET /api/auth/status': status } });
            const result = await page.run('checkAuth()');
            assert.equal(result, expected, name);
            assert.equal(wentToLogin(page), !expected, `${name}: login redirect`);
            if (!expected) {
                assert.equal(page.window.localStorage.getItem('jwt_token'), null, `${name}: token cleared`);
                assert.equal(page.window.localStorage.getItem('username'), null, `${name}: username cleared`);
            }
            const call = requests(page, 'GET', '/api/auth/status')[0];
            assert.equal(call.init.credentials, 'include', 'Authelia cookies are sent');
            if (typeof status === 'function') {
                assert.ok(consoleLines(page, 'error').some(line => line.startsWith('[AUTH] Error checking auth status:')));
            }
            page.close();
        }
    });
}

test('booting without a stored token goes to the login page and starts nothing else', async () => {
    const page = loadPage({
        url: 'http://localhost/index.html#/home',
        routes: { 'GET /api/auth/status': { autheliaEnabled: false, isAuthenticated: false } }
    });
    await page.settle();
    assert.deepEqual(page.navigations, [{ url: 'http://localhost/login.html', replace: false }]);
    assert.deepEqual(page.fetchCalls.map(call => call.path), ['/api/preferences', '/api/auth/status']);
    assert.equal(page.eventSources.length, 0);
});

test('booting with an expired token clears the session and goes to the login page', async () => {
    const page = loadPage({
        url: 'http://localhost/index.html#/home',
        localStorage: { jwt_token: expiredToken(), username: 'alice', client_cache_session_id: 'old-session' },
        routes: { 'GET /api/auth/status': { autheliaEnabled: false, isAuthenticated: false } }
    });
    await page.settle();
    assert.ok(wentToLogin(page));
    for (const key of ['jwt_token', 'username', 'authelia_authenticated', 'client_cache_session_id']) {
        assert.equal(page.window.localStorage.getItem(key), null, key);
    }
    assert.equal(requests(page, 'GET', '/api/version').length, 0);
});

test('an Authelia session is remembered and uses cookies plus an SSE token instead of a JWT', async () => {
    const page = await boot({
        localStorage: { jwt_token: '', username: '' },
        routes: {
            'GET /api/auth/status': { autheliaEnabled: true, isAuthenticated: true, username: 'bob', authMethod: 'authelia', requiresLogin: false },
            'GET /api/auth/sse-token': { token: 'sse token/1' }
        }
    });
    const storage = page.window.localStorage;
    assert.equal(storage.getItem('authelia_authenticated'), 'true');
    assert.equal(storage.getItem('username'), 'bob');
    assert.equal(page.$('#usernameDisplay').textContent, 'bob');
    assert.equal(page.$('#sidebarUsername').textContent, 'bob');
    assert.equal(page.$('#sidebarUser').hidden, false);

    const version = requests(page, 'GET', '/api/version')[0];
    assert.equal(version.headers.authorization, undefined);
    assert.equal(version.headers['x-client-session'], storage.getItem('client_cache_session_id'));

    const tokenCall = requests(page, 'GET', '/api/auth/sse-token')[0];
    assert.equal(tokenCall.init.credentials, 'include');
    assert.equal(page.eventSources.length, 1);
    assert.equal(page.eventSources[0].url, '/api/events/stream?access_token=sse%20token%2F1');
});

test('getAuthHeaders attaches the bearer token and one persistent client session id', () => {
    const token = validToken();
    const page = apiPage({ localStorage: { jwt_token: token } });
    const headers = page.evaluate('getAuthHeaders()');
    assert.equal(headers.Authorization, `Bearer ${token}`);
    assert.equal(headers['Content-Type'], 'application/json');
    assert.match(headers['X-Client-Session'], /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    assert.equal(page.window.localStorage.getItem('client_cache_session_id'), headers['X-Client-Session']);
    assert.equal(page.evaluate('getAuthHeaders()')['X-Client-Session'], headers['X-Client-Session']);
});

test('getAuthHeaders falls back to a time-based session id without crypto.randomUUID', () => {
    const page = apiPage({
        localStorage: { jwt_token: validToken() },
        beforeScripts(window) {
            Object.defineProperty(window.crypto, 'randomUUID', { value: undefined, configurable: true });
        }
    });
    assert.match(page.evaluate('getAuthHeaders()')['X-Client-Session'], /^[0-9a-z]+-[0-9a-z]+$/);
});

test('getAuthHeaders without a token sends no Authorization header and warns', () => {
    const page = apiPage();
    const headers = page.evaluate('getAuthHeaders()');
    assert.deepEqual(Object.keys(headers).sort(), ['Content-Type', 'X-Client-Session']);
    assert.ok(consoleLines(page, 'warn').includes('[AUTH] No JWT token found in localStorage'));
    assert.equal(wentToLogin(page), false);
});

test('getAuthHeaders with an expired token signs the user out instead of sending it', () => {
    const page = apiPage({ localStorage: { jwt_token: expiredToken(), username: 'alice' } });
    assert.deepEqual(page.evaluate('getAuthHeaders()'), {});
    assert.ok(wentToLogin(page));
    assert.equal(page.window.localStorage.getItem('jwt_token'), null);
});

test('getAuthHeaders for an Authelia session never sends a stored JWT', () => {
    const page = apiPage({ localStorage: { authelia_authenticated: 'true', jwt_token: validToken() } });
    const headers = page.evaluate('getAuthHeaders()');
    assert.deepEqual(Object.keys(headers).sort(), ['Content-Type', 'X-Client-Session']);
});

test('handleAuthError only treats 401 as a lost session', () => {
    const page = apiPage({ localStorage: { jwt_token: validToken() } });
    assert.equal(page.run('handleAuthError({ status: 403 })'), false);
    assert.equal(page.run('handleAuthError({ status: 500 })'), false);
    assert.equal(wentToLogin(page), false);
    assert.equal(page.run('handleAuthError({ status: 401 })'), true);
    assert.ok(wentToLogin(page));
    assert.equal(page.window.localStorage.getItem('jwt_token'), null);
});

test('redirectToLogin clears only the session keys and asks the service worker to drop cached images', () => {
    const page = apiPage({
        localStorage: {
            jwt_token: validToken(),
            username: 'alice',
            authelia_authenticated: 'true',
            client_cache_session_id: 'session-1',
            seriesLayout: 'grid'
        }
    });
    const posted = [];
    page.window.navigator.serviceWorker.controller = { postMessage: message => posted.push(plain(message)) };
    page.run('redirectToLogin()');
    assert.deepEqual(posted, [{ type: 'CLEAR_IMAGE_CACHE' }]);
    const storage = page.window.localStorage;
    for (const key of ['jwt_token', 'username', 'authelia_authenticated', 'client_cache_session_id']) {
        assert.equal(storage.getItem(key), null, key);
    }
    assert.equal(storage.getItem('seriesLayout'), 'grid');
    assert.deepEqual(page.navigations, [{ url: 'http://localhost/login.html', replace: false }]);
});

test('redirectToLogin still redirects when the image cache clear cannot be requested', () => {
    const page = apiPage({ localStorage: { jwt_token: validToken() } });
    page.window.navigator.serviceWorker.controller = {
        postMessage() { throw new Error('worker gone'); }
    };
    page.run('redirectToLogin()');
    assert.ok(consoleLines(page, 'log').some(line => line.startsWith('PWA: Failed to request image cache clear')));
    assert.ok(wentToLogin(page));

    const noWorker = apiPage({ serviceWorker: false, localStorage: { jwt_token: validToken() } });
    noWorker.run('redirectToLogin()');
    assert.ok(wentToLogin(noWorker));
});

test('the Log out button asks for confirmation before signing out', () => {
    const declined = apiPage({ confirm: false, localStorage: { jwt_token: validToken(), username: 'alice' } });
    declined.click('.sidebar-icon-btn--danger');
    assert.deepEqual(declined.dialogs, [{ kind: 'confirm', message: 'Are you sure you want to logout?' }]);
    assert.equal(wentToLogin(declined), false);
    assert.notEqual(declined.window.localStorage.getItem('jwt_token'), null);

    const confirmed = apiPage({ confirm: true, localStorage: { jwt_token: validToken(), username: 'alice' } });
    confirmed.click('.sidebar-icon-btn--danger');
    assert.ok(wentToLogin(confirmed));
    assert.equal(confirmed.window.localStorage.getItem('jwt_token'), null);
});
