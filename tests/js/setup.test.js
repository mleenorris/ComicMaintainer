'use strict';

// First-run setup (setup.html + js/pages/setup.js): creating the
// administrator account, the password strength meter, validation and the
// redirect away once setup is done.

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage, respond } = require('./support/dom');
const { deferred } = require('./support/app');

const networkError = () => {
    throw new TypeError('Failed to fetch');
};
const badGateway = respond(502, '<html><body>Bad Gateway</body></html>', { 'Content-Type': 'text/html' });

function openSetup({ routes = {}, ...options } = {}) {
    return loadPage({
        html: 'setup.html',
        url: 'http://localhost/setup.html',
        routes: { 'GET /api/auth/setup-required': { setupRequired: true }, ...routes },
        ...options
    });
}

const consoleLines = (page, type) => page.consoleMessages.filter(message => message.type === type).map(message => String(message.args[0]));
const setupCalls = page => page.fetchCalls.filter(call => call.path === '/api/auth/setup');
const theme = page => page.document.documentElement.getAttribute('data-theme');

function submit(page, { username = 'admin', password = 'Sup3r$ecret!', confirm = password } = {}) {
    page.input('#username', username);
    page.input('#password', password);
    page.input('#confirmPassword', confirm);
    page.click('#setupBtn');
}

function assertButtonReady(page, label) {
    assert.equal(page.$('#setupBtn').disabled, false, label);
    assert.equal(page.$('#setupBtnText').classList.contains('hidden'), false, label);
    assert.equal(page.$('#setupBtnSpinner').classList.contains('hidden'), true, label);
}

// --- on load -------------------------------------------------------------------

test('while setup is required the page applies the saved theme and stays', async () => {
    const page = openSetup({ localStorage: { theme: 'dark' } });
    await page.settle();

    assert.equal(theme(page), 'dark');
    assert.deepEqual(page.fetchCalls.map(call => `${call.method} ${call.url}`), ['GET /api/auth/setup-required']);
    assert.deepEqual(page.navigations, []);
    assert.deepEqual(page.errors, []);
});

test('the theme defaults to light and the toggle switches and remembers it', async () => {
    const page = openSetup();
    await page.settle();
    assert.equal(theme(page), 'light');

    page.click('.theme-toggle');
    assert.equal(theme(page), 'dark');
    assert.equal(page.window.localStorage.getItem('theme'), 'dark');

    page.click('.theme-toggle');
    assert.equal(theme(page), 'light');
    assert.equal(page.window.localStorage.getItem('theme'), 'light');
});

test('once an administrator exists the page sends the user to sign in', async () => {
    const page = openSetup({ routes: { 'GET /api/auth/setup-required': { setupRequired: false } } });
    await page.settle();

    assert.deepEqual(page.navigations, [{ url: 'http://localhost/login.html', replace: false }]);
});

test('a failed setup check is logged and the form stays', async () => {
    for (const answer of [networkError, badGateway]) {
        const page = openSetup({ routes: { 'GET /api/auth/setup-required': answer } });
        await page.settle();

        assert.ok(consoleLines(page, 'error').includes('Error checking setup status:'));
        assert.deepEqual(page.navigations, []);
    }
});

test('the strength meter rates the password as it is typed', async () => {
    const page = openSetup();
    await page.settle();
    const meter = page.$('#passwordStrength');
    const ratings = [
        ['abc', 'weak'],
        ['abcdefgh', 'weak'], // long enough, nothing else
        ['abcdefgh1', 'weak'], // + a digit
        ['Abcdefgh1', 'medium'], // + mixed case
        ['Abcdefgh1!', 'medium'], // + a symbol
        ['Abcdefgh1!xy', 'strong'], // + 12 characters
        ['ABCDEFGH1!XY', 'medium'], // upper case only
        ['', 'weak']
    ];

    for (const [password, rating] of ratings) {
        page.input('#password', password);
        const classes = ['weak', 'medium', 'strong'].filter(name => meter.classList.contains(name));
        assert.deepEqual(classes, [rating], password);
    }
});

test('invalid details are explained and never sent', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = openSetup();
    await page.settle();
    const cases = [
        [{ username: '   ' }, 'Please fill in all fields'],
        [{ username: '  ab  ' }, 'Username must be at least 3 characters'],
        [{ password: 'Ab1!xyz' }, 'Password must be at least 8 characters'],
        [{ password: 'Sup3r$ecret!', confirm: 'Sup3r$ecret?' }, 'Passwords do not match']
    ];

    for (const [details, message] of cases) {
        submit(page, details);
        await page.settle();
        const error = page.$('#errorMessage');
        assert.equal(error.textContent, message);
        assert.equal(error.classList.contains('show'), true, message);
        assertButtonReady(page, message);
    }
    assert.deepEqual(setupCalls(page), []);

    // Empty required fields: the browser does not submit at all.
    const fresh = openSetup();
    await fresh.settle();
    fresh.click('#setupBtn');
    await fresh.settle();
    assert.equal(fresh.$('#errorMessage').textContent, '');
    assert.deepEqual(setupCalls(fresh), []);
});

test('the page itself also refuses an empty password confirmation', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = openSetup();
    await page.settle();
    page.input('#username', 'admin');
    page.input('#password', 'Sup3r$ecret!');

    // A submit that bypasses the browser's required-field check.
    page.$('#setupForm').dispatchEvent(new page.window.Event('submit', { cancelable: true }));
    await page.settle();

    assert.equal(page.$('#errorMessage').textContent, 'Please fill in all fields');
    assert.deepEqual(setupCalls(page), []);
});

// --- creating the account ------------------------------------------------------

test('creating the administrator sends the details, confirms and goes to sign in', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const gate = deferred();
    const page = openSetup({ routes: { 'POST /api/auth/setup': () => gate.promise } });
    await page.settle();

    submit(page, { username: '  admin  ', password: 'Sup3r$ecret!' });
    await page.settle();

    assert.equal(page.$('#setupBtn').disabled, true);
    assert.equal(page.$('#setupBtnText').classList.contains('hidden'), true);
    assert.equal(page.$('#setupBtnSpinner').classList.contains('hidden'), false);
    const [call] = setupCalls(page);
    assert.equal(call.method, 'POST');
    assert.equal(call.headers['content-type'], 'application/json');
    assert.deepEqual(call.body, { username: 'admin', password: 'Sup3r$ecret!', email: null });

    gate.resolve({ message: 'Admin user created successfully' });
    await page.settle();

    const success = page.$('#successMessage');
    assert.equal(success.textContent, 'Administrator account created successfully! Redirecting to login...');
    assert.equal(success.classList.contains('show'), true);
    // The account exists now: the button stays disabled so it cannot be created twice.
    assert.equal(page.$('#setupBtn').disabled, true);
    assert.deepEqual(page.navigations, []);

    t.mock.timers.tick(1500);
    assert.deepEqual(page.navigations, [{ url: 'http://localhost/login.html', replace: false }]);
    t.mock.timers.tick(1500);
    assert.equal(success.textContent, '');
    assert.equal(success.classList.contains('show'), false);
});

test('a rejected setup shows the reason and lets the user try again', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const cases = [
        [respond(400, { error: 'Admin user already exists' }), 'Admin user already exists'],
        [respond(500, { title: 'Internal Server Error' }), 'Setup failed. Please try again.']
    ];
    for (const [answer, message] of cases) {
        const page = openSetup({ routes: { 'POST /api/auth/setup': answer } });
        await page.settle();

        submit(page);
        await page.settle();

        const error = page.$('#errorMessage');
        assert.equal(error.textContent, message);
        assertButtonReady(page, message);
        assert.deepEqual(page.navigations, [], message);

        t.mock.timers.tick(5000);
        assert.equal(error.textContent, '', message);
        assert.equal(error.classList.contains('show'), false, message);
        page.close();
    }
});

test('a network failure or an unreadable answer shows a generic error', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    for (const answer of [networkError, badGateway]) {
        const page = openSetup({ routes: { 'POST /api/auth/setup': answer } });
        await page.settle();

        submit(page);
        await page.settle();

        assert.equal(page.$('#errorMessage').textContent, 'An error occurred. Please try again.');
        assert.ok(consoleLines(page, 'error').includes('Setup error:'));
        assertButtonReady(page);
        assert.deepEqual(page.navigations, []);
        page.close();
    }
});

test('an error shown again stays for its full five seconds', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = openSetup();
    await page.settle();
    const error = page.$('#errorMessage');

    submit(page, { confirm: 'Sup3r$ecret?' });
    await page.settle();
    assert.equal(error.textContent, 'Passwords do not match');
    t.mock.timers.tick(3000);
    submit(page, { username: '  ab  ' });
    await page.settle();

    // The first error's timer, due two seconds in, must not hide the second.
    t.mock.timers.tick(4999);
    assert.equal(error.textContent, 'Username must be at least 3 characters');
    assert.equal(error.classList.contains('show'), true);
    t.mock.timers.tick(1);
    assert.equal(error.textContent, '');
    assert.equal(error.classList.contains('show'), false);
});

test('a newer success message is not hidden by the timer of an earlier one', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = openSetup();
    await page.settle();
    const success = page.$('#successMessage');

    page.run("showSuccess('First')");
    t.mock.timers.tick(2000);
    page.run("showSuccess('Second')");

    t.mock.timers.tick(2999);
    assert.equal(success.textContent, 'Second');
    assert.equal(success.classList.contains('show'), true);
    t.mock.timers.tick(1);
    assert.equal(success.textContent, '');
    assert.equal(success.classList.contains('show'), false);
});

test('API paths are resolved from the site root', () => {
    const page = openSetup();

    assert.equal(page.run("apiUrl('api/auth/setup')"), '/api/auth/setup');
    assert.equal(page.run("apiUrl('/api/auth/setup')"), '/api/auth/setup');
});
