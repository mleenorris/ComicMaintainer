'use strict';

// js/app/events.js — the Server-Sent Events connection: opening the stream
// for JWT and Authelia sessions, reconnect backoff, giving up, auth expiry and
// cleanup on unload.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { jwt, validToken } = require('./support/app');
const { boot, consoleLines, messages, requests, wentToLogin } = require('./support/core-helpers');

const UNAVAILABLE = 'Real-time updates unavailable. Please refresh the page.';
const AUTHELIA_SESSION = { autheliaEnabled: true, isAuthenticated: true, username: 'bob', authMethod: 'authelia', requiresLogin: false };
const JOB_ID = '6c0f8f2e-4a8b-4d55-9a4e-2f1d3c5b7a90';

const expiredToken = () => jwt({ sub: 'alice', exp: Math.floor(Date.now() / 1000) - 60 });
const useMockTimers = t => t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });

function bootAuthelia(routes = {}) {
    return boot({
        localStorage: { jwt_token: '', username: '' },
        routes: { 'GET /api/auth/status': AUTHELIA_SESSION, 'GET /api/auth/sse-token': { token: 'sse-token-1' }, ...routes }
    });
}

/** The server closes the stream (EventSource gives up: readyState CLOSED + error). */
async function dropStream(page) {
    const source = page.eventSources.at(-1);
    source.readyState = 2;
    source.emit('error');
    await page.settle(3);
    return source;
}

const retryDelays = page => consoleLines(page, 'warn')
    .map(line => /^SSE: Connection closed \(attempt (\d+)\/20\), will retry in ([\d.]+)s$/.exec(line))
    .filter(Boolean)
    .map(match => `${match[1]}@${match[2]}s`);

test('the stream is opened with the stored session token once the session is verified', async () => {
    // A signature with characters that must be escaped in a query string.
    const token = `${validToken().split('.').slice(0, 2).join('.')}.sig+/=`;
    const page = await boot({ localStorage: { jwt_token: token } });
    assert.equal(page.eventSources.length, 1);
    assert.equal(page.eventSources[0].url, `/api/events/stream?access_token=${encodeURIComponent(token)}`);
    const logs = consoleLines(page, 'log');
    assert.ok(logs.includes('SSE: Authentication mode - JWT'));
    assert.ok(logs.includes('SSE: Existing token - Present'));
    page.eventSources[0].emit('open');
    assert.ok(consoleLines(page, 'log').includes('SSE: Connected to event stream'));
    assert.equal(requests(page, 'GET', '/api/auth/sse-token').length, 0);
});

test('a closed stream is reopened with growing delays, reset by a successful connection', async t => {
    useMockTimers(t);
    const page = await boot();
    const first = await dropStream(page);
    assert.equal(first.readyState, 2, 'the dead stream is closed so the browser stops retrying it');
    assert.deepEqual(retryDelays(page), ['1@5s']);

    t.mock.timers.tick(4999);
    await page.settle(2);
    assert.equal(page.eventSources.length, 1);
    t.mock.timers.tick(1);
    await page.settle(2);
    assert.equal(page.eventSources.length, 2);

    await dropStream(page);
    t.mock.timers.tick(7500);
    await page.settle(2);
    assert.equal(page.eventSources.length, 3);
    assert.deepEqual(retryDelays(page), ['1@5s', '2@7.5s']);

    page.eventSources[2].emit('open');
    await dropStream(page);
    assert.deepEqual(retryDelays(page), ['1@5s', '2@7.5s', '1@5s']);
    t.mock.timers.tick(5000);
    await page.settle(2);
    assert.equal(page.eventSources.length, 4);
    assert.equal(wentToLogin(page), false);
});

test('while the browser reconnects by itself the app only logs', async t => {
    useMockTimers(t);
    const page = await boot();
    const source = page.eventSources[0];
    source.readyState = 0;
    source.emit('error');
    source.readyState = 1;
    source.emit('error');
    await page.settle(2);
    assert.deepEqual(consoleLines(page, 'log').filter(line => line === 'SSE: Reconnecting...'), ['SSE: Reconnecting...']);
    t.mock.timers.tick(60000);
    await page.settle(2);
    assert.equal(page.eventSources.length, 1);
    assert.equal(source.readyState, 1);
});

test('after twenty consecutive drops the app stops reconnecting and says so', async t => {
    useMockTimers(t);
    const page = await boot();
    for (let attempt = 1; attempt <= 20; attempt++) {
        await dropStream(page);
        t.mock.timers.tick(60000);
        await page.settle(2);
    }
    assert.equal(page.eventSources.length, 20);
    assert.deepEqual(retryDelays(page).slice(0, 9), [
        '1@5s', '2@7.5s', '3@11.25s', '4@16.875s', '5@25.3125s', '6@37.96875s', '7@56.953125s', '8@60s', '9@60s'
    ]);
    assert.equal(retryDelays(page).at(-1), '20@60s');
    assert.ok(consoleLines(page, 'error').includes('SSE: Maximum retry attempts reached. Please refresh the page or check your connection.'));
    assert.deepEqual(messages(page).filter(message => message.text === UNAVAILABLE), [{ type: 'error', text: UNAVAILABLE, role: 'alert' }]);
});

test('a stream dropped after the JWT expired sends the user to the login page', async t => {
    useMockTimers(t);
    const page = await boot();
    page.window.localStorage.setItem('jwt_token', expiredToken());
    await dropStream(page);
    assert.ok(consoleLines(page, 'warn').includes('[AUTH] JWT token expired'));
    assert.ok(consoleLines(page, 'warn').includes('SSE: Authentication expired during connection'));
    assert.ok(wentToLogin(page));
    t.mock.timers.tick(60000);
    await page.settle(2);
    assert.equal(page.eventSources.length, 1, 'no reconnect is scheduled');
});

test('a stream dropped after the token was removed sends the user to the login page', async () => {
    const page = await boot();
    page.window.localStorage.removeItem('jwt_token');
    await dropStream(page);
    assert.ok(consoleLines(page, 'warn').includes('[AUTH] No JWT token found'));
    assert.ok(wentToLogin(page));
});

test('reconnecting without a valid session closes the old stream and goes to login', async () => {
    const page = await boot();
    const [source] = page.eventSources;
    page.window.localStorage.setItem('jwt_token', expiredToken());
    await page.run('initEventSource()');
    assert.equal(source.readyState, 2);
    assert.equal(page.eventSources.length, 1);
    assert.ok(consoleLines(page, 'error').includes('SSE: Authentication check failed - cannot establish SSE connection'));
    assert.ok(wentToLogin(page));
});

test('Authelia sessions fetch a dedicated SSE token with their cookies', async () => {
    const page = await bootAuthelia();
    const [call] = requests(page, 'GET', '/api/auth/sse-token');
    assert.equal(call.init.credentials, 'include');
    assert.equal(call.headers.authorization, undefined);
    assert.equal(page.eventSources[0].url, '/api/events/stream?access_token=sse-token-1');
    const logs = consoleLines(page, 'log');
    assert.ok(logs.includes('SSE: Authentication mode - Authelia'));
    assert.ok(logs.includes('SSE: Existing token - None'));
    assert.ok(logs.includes('SSE: Received SSE token for Authelia user'));
});

test('a refused SSE token is retried with backoff until the server issues one', async t => {
    useMockTimers(t);
    const page = await bootAuthelia({ 'GET /api/auth/sse-token': respond(401, { error: 'Unauthorized' }) });
    assert.equal(page.eventSources.length, 0);
    const errors = consoleLines(page, 'error');
    assert.ok(errors.includes('SSE: Failed to get SSE token - status: 401'));
    assert.ok(errors.includes('SSE: Authentication failed. SSE connection cannot be established.'));
    assert.ok(errors.some(line => line === 'SSE: Error fetching SSE token: Authentication failed (401). Please check Authelia configuration.'));
    assert.ok(consoleLines(page, 'log').includes('SSE: Will retry in 5s (attempt 1/20)'));

    page.route('GET /api/auth/sse-token', { token: 'second try' });
    t.mock.timers.tick(5000);
    await page.settle(3);
    assert.equal(page.eventSources.length, 1);
    assert.equal(page.eventSources[0].url, '/api/events/stream?access_token=second%20try');
    assert.equal(wentToLogin(page), false);
});

test('when no SSE token can be obtained the app gives up after twenty attempts', async t => {
    useMockTimers(t);
    const page = await bootAuthelia({ 'GET /api/auth/sse-token': 500 });
    assert.ok(consoleLines(page, 'error').includes('SSE: Failed to initialize EventSource: No authentication token available for SSE connection'));
    for (let attempt = 2; attempt <= 20; attempt++) {
        t.mock.timers.tick(60000);
        await page.settle(3);
    }
    assert.equal(requests(page, 'GET', '/api/auth/sse-token').length, 20);
    assert.equal(page.eventSources.length, 0);
    assert.ok(consoleLines(page, 'log').includes('SSE: Will retry in 60s (attempt 19/20)'));
    assert.ok(consoleLines(page, 'error').includes('SSE: Maximum retry attempts reached'));
    assert.deepEqual(messages(page).filter(message => message.text === UNAVAILABLE).length, 1);

    t.mock.timers.tick(60000);
    await page.settle(3);
    assert.equal(requests(page, 'GET', '/api/auth/sse-token').length, 20, 'nothing further is attempted');
});

for (const [description, status, expectedLog] of [
    ['reports the Authelia session as ended', { autheliaEnabled: true, isAuthenticated: false }, ['warn', '[AUTH] Authelia session expired or invalid']],
    ['fails', 503, ['warn', '[AUTH] Authelia session expired or invalid']],
    ['is unreachable', () => { throw new TypeError('Failed to fetch'); }, ['error', '[AUTH] Error checking authentication status: Failed to fetch']]
]) {
    test(`a dropped Authelia stream goes to login when the session check ${description}`, async () => {
        const page = await bootAuthelia();
        page.route('GET /api/auth/status', status);
        await dropStream(page);
        assert.ok(consoleLines(page, expectedLog[0]).includes(expectedLog[1]));
        assert.ok(wentToLogin(page));
    });
}

test('a dropped Authelia stream with a live session is reconnected', async t => {
    useMockTimers(t);
    const page = await bootAuthelia();
    await dropStream(page);
    assert.equal(wentToLogin(page), false);
    t.mock.timers.tick(5000);
    await page.settle(3);
    assert.equal(page.eventSources.length, 2);
    assert.equal(requests(page, 'GET', '/api/auth/sse-token').length, 2);
});

test('leaving the page cancels a pending reconnect and closes the stream', async t => {
    useMockTimers(t);
    const page = await boot();
    await dropStream(page);
    page.window.dispatchEvent(new page.window.Event('beforeunload', { cancelable: true }));
    t.mock.timers.tick(60000);
    await page.settle(2);
    assert.equal(page.eventSources.length, 1);

    const second = await boot();
    const [source] = second.eventSources;
    const unload = new second.window.Event('beforeunload', { cancelable: true });
    second.window.dispatchEvent(unload);
    assert.equal(source.readyState, 2);
    assert.equal(unload.defaultPrevented, false, 'no job is running, so leaving is not questioned');
    assert.doesNotThrow(() => second.run('cleanupEventSource()'));
});

test('reconnecting while a job runs fetches the job status missed while disconnected', async () => {
    const job = {
        job_id: JOB_ID,
        status: 'running',
        total_items: 4,
        processed_items: 1,
        failed_items: 0,
        current_file: 'Saga/Saga 001.cbz',
        start_time: 1700000000,
        end_time: null,
        results: [{ file: 'Saga/Saga 001.cbz', success: true, error: null }]
    };
    const page = await boot({
        routes: {
            'GET /api/active-job': { job_id: JOB_ID, job_title: 'Processing 4 files...', status: 'running', total_items: 4, processed_items: 1, failed_items: 0, current_file: 'Saga/Saga 001.cbz', start_time: 1700000000, end_time: null },
            [`GET /api/jobs/${JOB_ID}`]: job
        }
    });
    const before = requests(page, 'GET', `/api/jobs/${JOB_ID}`).length;
    page.route(`GET /api/jobs/${JOB_ID}`, { ...job, processed_items: 3, current_file: 'Saga/Saga 003.cbz' });
    page.eventSources[0].emit('open');
    await page.settle(3);
    assert.equal(requests(page, 'GET', `/api/jobs/${JOB_ID}`).length, before + 1);
    assert.ok(consoleLines(page, 'log').includes(`SSE: Reconnected with active job ${JOB_ID}, fetching current status...`));
    assert.equal(page.$('#progressText').textContent, '3 / 4 files');
});

test('malformed stream messages are logged and ignored', async () => {
    const page = await boot();
    page.eventSources[0].emit('message', '{not json');
    assert.ok(consoleLines(page, 'error').some(line => line.startsWith('SSE: Error parsing event data:')));
    page.eventSources[0].emit('message', { type: 'library_exploded', data: {} });
    assert.ok(consoleLines(page, 'log').includes('SSE: Unknown event type: library_exploded'));
});
