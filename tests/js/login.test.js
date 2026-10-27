'use strict';

// The sign-in page (login.html + js/pages/login.js): the checks it makes on
// load (first-run setup, an Authelia or stored-token session), signing in,
// and the messages it shows.

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage, respond } = require('./support/dom');
const { jwt, validToken, deferred } = require('./support/app');

const SIGNED_OUT = { autheliaEnabled: false, isAuthenticated: false, username: null, authMethod: 'jwt', requiresLogin: true };
const networkError = () => {
    throw new TypeError('Failed to fetch');
};
const badGateway = respond(502, '<html><body>Bad Gateway</body></html>', { 'Content-Type': 'text/html' });

function openLogin({ routes = {}, ...options } = {}) {
    return loadPage({
        html: 'login.html',
        url: 'http://localhost/login.html',
        routes: {
            'GET /api/auth/setup-required': { setupRequired: false },
            'GET /api/auth/status': SIGNED_OUT,
            ...routes
        },
        ...options
    });
}

const consoleLines = (page, type) => page.consoleMessages.filter(message => message.type === type).map(message => String(message.args[0]));
const loginCalls = page => page.fetchCalls.filter(call => call.path === '/api/auth/login');
const theme = page => page.document.documentElement.getAttribute('data-theme');

function signIn(page, username, password) {
    page.input('#username', username);
    page.input('#password', password);
    page.click('#loginBtn');
}

function assertButtonReady(page, label) {
    assert.equal(page.$('#loginBtn').disabled, false, label);
    assert.equal(page.$('#loginBtnText').classList.contains('hidden'), false, label);
    assert.equal(page.$('#loginBtnSpinner').classList.contains('hidden'), true, label);
}

// --- on load -------------------------------------------------------------------

test('on load the page applies the saved theme and checks for first-run setup and an existing session', async () => {
    const page = openLogin({ localStorage: { theme: 'dark' } });
    await page.settle();

    assert.equal(theme(page), 'dark');
    assert.deepEqual(page.fetchCalls.map(call => `${call.method} ${call.url}`), ['GET /api/auth/setup-required', 'GET /api/auth/status']);
    // Cookies go along, so a session held by an auth proxy (Authelia) is seen.
    assert.equal(page.fetchCalls[1].init.credentials, 'include');
    assert.deepEqual(page.navigations, []);
    assert.deepEqual(page.errors, []);
});

test('the theme defaults to light and the toggle switches and remembers it', async () => {
    const page = openLogin();
    await page.settle();
    assert.equal(theme(page), 'light');

    page.click('.theme-toggle');
    assert.equal(theme(page), 'dark');
    assert.equal(page.window.localStorage.getItem('theme'), 'dark');

    page.click('.theme-toggle');
    assert.equal(theme(page), 'light');
    assert.equal(page.window.localStorage.getItem('theme'), 'light');
});

test('before an administrator exists the page sends the user to first-run setup', async () => {
    const page = openLogin({ routes: { 'GET /api/auth/setup-required': { setupRequired: true } } });
    await page.settle();

    assert.deepEqual(page.navigations, [{ url: 'http://localhost/setup.html', replace: false }]);
});

test('a failed setup check is logged and the user stays on the sign-in page', async () => {
    for (const answer of [networkError, badGateway]) {
        const page = openLogin({ routes: { 'GET /api/auth/setup-required': answer } });
        await page.settle();

        assert.ok(consoleLines(page, 'error').includes('Error checking setup status:'));
        assert.deepEqual(page.navigations, []);
    }
});

test('a user already signed in through Authelia is sent to the app', async () => {
    const page = openLogin({
        routes: { 'GET /api/auth/status': { autheliaEnabled: true, isAuthenticated: true, username: 'bob', authMethod: 'authelia', requiresLogin: false } }
    });
    await page.settle();

    assert.equal(page.window.localStorage.getItem('authelia_authenticated'), 'true');
    assert.equal(page.window.localStorage.getItem('username'), 'bob');
    assert.deepEqual(page.navigations, [{ url: 'http://localhost/', replace: false }]);
});

test('with Authelia enabled but no Authelia session the sign-in form stays', async () => {
    const page = openLogin({
        routes: { 'GET /api/auth/status': { autheliaEnabled: true, isAuthenticated: false, username: null, authMethod: 'jwt', requiresLogin: true } }
    });
    await page.settle();

    assert.equal(page.window.localStorage.getItem('authelia_authenticated'), null);
    assert.deepEqual(page.navigations, []);
});

test('a stored, unexpired session token skips the sign-in form', async () => {
    // Non-ASCII claims must decode too.
    const page = openLogin({ localStorage: { jwt_token: validToken({ name: 'Zoë' }), username: 'alice' } });
    await page.settle();

    assert.deepEqual(page.navigations, [{ url: 'http://localhost/', replace: false }]);
    assert.equal(page.window.localStorage.getItem('username'), 'alice');
});

test('when the session check fails the stored token still decides', async () => {
    for (const status of [networkError, respond(500, { error: 'boom' })]) {
        const page = openLogin({ localStorage: { jwt_token: validToken() }, routes: { 'GET /api/auth/status': status } });
        await page.settle();

        assert.deepEqual(page.navigations, [{ url: 'http://localhost/', replace: false }]);
        assert.equal(consoleLines(page, 'error').includes('[LOGIN] Error checking auth status:'), status === networkError);
    }
});

test('an expired or unreadable stored token is cleared and the sign-in form stays', async () => {
    const tokens = [
        ['expired', jwt({ sub: 'alice', exp: Math.floor(Date.now() / 1000) - 60 }), false],
        ['without an expiry', jwt({ sub: 'alice' }), false],
        ['not a JWT', 'opaque-session-token', false],
        ['with a corrupt payload', 'header.%%%.signature', true],
        ['with a non-JSON payload', `header.${Buffer.from('not json').toString('base64url')}.signature`, true]
    ];
    for (const [label, token, undecodable] of tokens) {
        const page = openLogin({ localStorage: { jwt_token: token, username: 'alice' } });
        await page.settle();

        assert.deepEqual(page.navigations, [], label);
        assert.equal(page.window.localStorage.getItem('jwt_token'), null, label);
        assert.equal(page.window.localStorage.getItem('username'), null, label);
        assert.ok(consoleLines(page, 'log').includes('JWT token has expired, clearing storage'), label);
        assert.equal(consoleLines(page, 'error').includes('Error decoding JWT token:'), undecodable, label);
    }
});

test('API paths are resolved from the site root', () => {
    const page = openLogin();

    assert.equal(page.run("apiUrl('api/auth/status')"), '/api/auth/status');
    assert.equal(page.run("apiUrl('/api/auth/status')"), '/api/auth/status');
});

// --- signing in ----------------------------------------------------------------

test('signing in stores the token and user name, confirms, then opens the app', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const gate = deferred();
    const page = openLogin({ routes: { 'POST /api/auth/login': () => gate.promise } });
    await page.settle();

    signIn(page, '  alice  ', 'correct horse');
    await page.settle();

    // While the request is in flight the button shows a spinner and cannot be pressed again.
    assert.equal(page.$('#loginBtn').disabled, true);
    assert.equal(page.$('#loginBtnText').classList.contains('hidden'), true);
    assert.equal(page.$('#loginBtnSpinner').classList.contains('hidden'), false);
    const [call] = loginCalls(page);
    assert.equal(call.method, 'POST');
    assert.equal(call.headers['content-type'], 'application/json');
    assert.deepEqual(call.body, { username: 'alice', password: 'correct horse' });

    gate.resolve({ token: 'jwt-for-alice' });
    await page.settle();

    assert.equal(page.window.localStorage.getItem('jwt_token'), 'jwt-for-alice');
    assert.equal(page.window.localStorage.getItem('username'), 'alice');
    const success = page.$('#successMessage');
    assert.equal(success.textContent, 'Login successful! Redirecting...');
    assert.equal(success.classList.contains('show'), true);
    assert.equal(page.$('#errorMessage').textContent, '');
    assert.deepEqual(page.navigations, []);

    t.mock.timers.tick(500);
    assert.deepEqual(page.navigations, [{ url: 'http://localhost/', replace: false }]);
    t.mock.timers.tick(2500);
    assert.equal(success.textContent, '');
    assert.equal(success.classList.contains('show'), false);
});

test('a rejected sign-in shows the reason and lets the user try again', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const cases = [
        [respond(401, { error: 'Invalid username or password' }), 'Invalid username or password'],
        [respond(401, { error: 'Account is disabled' }), 'Account is disabled'],
        [respond(400, { title: 'One or more validation errors occurred.' }), 'Login failed. Please check your credentials.'],
        // An OK answer without a token is no sign-in either.
        [{ message: 'Welcome' }, 'Login failed. Please check your credentials.']
    ];
    for (const [answer, message] of cases) {
        const page = openLogin({ routes: { 'POST /api/auth/login': answer } });
        await page.settle();

        signIn(page, 'alice', 'wrong');
        await page.settle();

        const error = page.$('#errorMessage');
        assert.equal(error.textContent, message);
        assert.equal(error.classList.contains('show'), true, message);
        assertButtonReady(page, message);
        assert.equal(page.window.localStorage.getItem('jwt_token'), null, message);
        assert.deepEqual(page.navigations, [], message);
        page.close();
    }
});

test('a network failure or an unreadable answer shows a generic error', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    for (const answer of [networkError, badGateway]) {
        const page = openLogin({ routes: { 'POST /api/auth/login': answer } });
        await page.settle();

        signIn(page, 'alice', 'secret');
        await page.settle();

        assert.equal(page.$('#errorMessage').textContent, 'An error occurred. Please try again.');
        assert.ok(consoleLines(page, 'error').includes('Login error:'));
        assertButtonReady(page);
        assert.deepEqual(page.navigations, []);
        page.close();
    }
});

test('an error message disappears after five seconds', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = openLogin({ routes: { 'POST /api/auth/login': respond(401, { error: 'Invalid username or password' }) } });
    await page.settle();
    signIn(page, 'alice', 'wrong');
    await page.settle();
    const error = page.$('#errorMessage');

    t.mock.timers.tick(4999);
    assert.equal(error.textContent, 'Invalid username or password');
    t.mock.timers.tick(1);
    assert.equal(error.textContent, '');
    assert.equal(error.classList.contains('show'), false);
});

test('blank credentials are not sent', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = openLogin();
    await page.settle();

    // With the required fields empty the browser does not submit at all.
    page.click('#loginBtn');
    await page.settle();
    assert.equal(page.$('#errorMessage').textContent, '');

    // A user name of only spaces passes the browser's check but not the page's.
    signIn(page, '   ', 'secret');
    await page.settle();
    assert.equal(page.$('#errorMessage').textContent, 'Please enter both username and password');
    assert.deepEqual(loginCalls(page), []);
    assertButtonReady(page);
});
