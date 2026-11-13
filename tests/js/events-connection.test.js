'use strict';

// js/app/events.js — the Server-Sent Events connection: opening the stream
// with a single-use stream token for JWT and Authelia sessions, reconnecting
// with backoff and a fresh token, replacing abandoned streams, polling the
// active job while the stream is down, re-arming on online / visibility /
// back-forward cache, auth expiry and cleanup when the page goes away.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred, jwt, validToken } = require('./support/app');
const { boot, consoleLines, messages, requests, wentToLogin } = require('./support/core-helpers');

const UNAVAILABLE = 'Real-time updates unavailable. Retrying in the background...';
const RESTORED = 'Real-time updates are back.';
const AUTHELIA_SESSION = { autheliaEnabled: true, isAuthenticated: true, username: 'bob', authMethod: 'authelia', requiresLogin: false };
const JOB_ID = '6c0f8f2e-4a8b-4d55-9a4e-2f1d3c5b7a90';
const STREAM_TOKEN = 'POST /api/auth/stream-token';

const expiredToken = () => jwt({ sub: 'alice', exp: Math.floor(Date.now() / 1000) - 60 });
const useMockTimers = t => t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });

function bootAuthelia(routes = {}) {
    return boot({
        localStorage: { jwt_token: '', username: '' },
        routes: { 'GET /api/auth/status': AUTHELIA_SESSION, ...routes }
    });
}

/** The running job of the page, as GET /api/active-job and GET /api/jobs/{id} report it. */
function activeJobRoutes(processed = 1) {
    const job = {
        job_id: JOB_ID,
        status: 'running',
        total_items: 4,
        processed_items: processed,
        failed_items: 0,
        current_file: `Saga/Saga 00${processed}.cbz`,
        start_time: 1700000000,
        end_time: null,
        results: [{ file: 'Saga/Saga 001.cbz', success: true, error: null }]
    };
    return {
        job,
        routes: {
            'GET /api/active-job': { ...job, job_title: 'Processing 4 files...', results: undefined },
            [`GET /api/jobs/${JOB_ID}`]: job
        }
    };
}

/** The connection fails (the server closes it, a proxy answers 502, the network drops). */
async function dropStream(page, readyState = 2) {
    const source = page.eventSources.at(-1);
    source.readyState = readyState;
    source.emit('error');
    await page.settle(3);
    return source;
}

/** Advances the mocked clock one watchdog period (5 s) at a time; jsdom re-arms intervals as chained timeouts. */
async function advance(t, page, ms) {
    for (let elapsed = 0; elapsed < ms; elapsed += 5000) {
        t.mock.timers.tick(Math.min(5000, ms - elapsed));
        await page.settle(3);
    }
}

const retryDelays = page => consoleLines(page, 'warn')
    .map(line => /^SSE: Will retry in ([\d.]+)s \(attempt (\d+)\)$/.exec(line))
    .filter(Boolean)
    .map(match => `${match[2]}@${match[1]}s`);

test('the stream is opened with a fresh single-use stream token, never the session token', async () => {
    const session = validToken();
    const page = await boot({ localStorage: { jwt_token: session } });
    const [call] = requests(page, 'POST', '/api/auth/stream-token');
    assert.equal(call.headers.authorization, `Bearer ${session}`);
    assert.equal(call.init.credentials, 'include');
    assert.equal(page.eventSources.length, 1);
    assert.equal(page.eventSources[0].url, '/api/events/stream?access_token=stream-token-1');
    assert.ok(!page.eventSources[0].url.includes(session), 'proxies log URLs: the session token must not be in one');
    assert.ok(consoleLines(page, 'log').includes('SSE: Authentication mode - JWT'));
    page.eventSources[0].emit('open');
    assert.ok(consoleLines(page, 'log').includes('SSE: Connected to event stream'));
});

test('a stream token with characters that must be escaped is encoded in the URL', async () => {
    const page = await boot({ routes: { [STREAM_TOKEN]: { token: 'a.b.sig+/=', expiresIn: 60 } } });
    assert.equal(page.eventSources[0].url, `/api/events/stream?access_token=${encodeURIComponent('a.b.sig+/=')}`);
});

test('every reconnect gets a new stream token, with growing delays reset by a successful connection', async t => {
    useMockTimers(t);
    const page = await boot();
    const first = await dropStream(page);
    assert.equal(first.readyState, 2, 'the dead stream is closed so the browser stops retrying it');
    assert.deepEqual(retryDelays(page), ['1@5s']);

    t.mock.timers.tick(4999);
    await page.settle(2);
    assert.equal(page.eventSources.length, 1);
    t.mock.timers.tick(1);
    await page.settle(3);
    assert.equal(page.eventSources.length, 2);
    assert.equal(page.eventSources[1].url, '/api/events/stream?access_token=stream-token-2');

    await dropStream(page);
    t.mock.timers.tick(7500);
    await page.settle(3);
    assert.equal(page.eventSources.length, 3);
    assert.deepEqual(retryDelays(page), ['1@5s', '2@7.5s']);

    page.eventSources[2].emit('open');
    await dropStream(page);
    assert.deepEqual(retryDelays(page), ['1@5s', '2@7.5s', '1@5s']);
    t.mock.timers.tick(5000);
    await page.settle(3);
    assert.equal(page.eventSources.length, 4);
    assert.equal(requests(page, 'POST', '/api/auth/stream-token').length, 4);
    assert.equal(wentToLogin(page), false);
});

test('an error while the browser would retry by itself also gets a new stream token', async t => {
    // The used token is refused, so the browser's own retry of the same URL could never succeed.
    useMockTimers(t);
    const page = await boot();
    const first = await dropStream(page, 0);
    assert.equal(first.readyState, 2, 'the browser is stopped from retrying with the used token');
    assert.ok(consoleLines(page, 'warn').includes('SSE: Connection lost (readyState 0)'));
    t.mock.timers.tick(5000);
    await page.settle(3);
    assert.equal(page.eventSources.length, 2);
    assert.equal(page.eventSources[1].url, '/api/events/stream?access_token=stream-token-2');
});

test('after twenty failures the user is told once, retries go on at the longest delay, and recovery is announced', async t => {
    useMockTimers(t);
    const page = await boot();
    const failures = async count => {
        for (let attempt = 0; attempt < count; attempt++) {
            await dropStream(page);
            t.mock.timers.tick(60000);
            await page.settle(3);
        }
    };
    const told = () => consoleLines(page, 'error').filter(line => line === 'SSE: 20 consecutive connection failures; still retrying every 60s at most');

    await failures(19);
    assert.deepEqual(told(), []);
    await dropStream(page);
    assert.equal(told().length, 1);
    assert.deepEqual(messages(page).filter(message => message.text === UNAVAILABLE), [{ type: 'error', text: UNAVAILABLE, role: 'alert' }]);
    t.mock.timers.tick(60000);
    await page.settle(3);

    await failures(4);
    assert.equal(page.eventSources.length, 25, 'the app never stops reconnecting');
    assert.deepEqual(retryDelays(page).slice(0, 9), [
        '1@5s', '2@7.5s', '3@11.25s', '4@16.875s', '5@25.3125s', '6@37.96875s', '7@56.953125s', '8@60s', '9@60s'
    ]);
    assert.equal(retryDelays(page).at(-1), '24@60s');
    assert.equal(told().length, 1, 'the user is told once');

    page.eventSources.at(-1).emit('open');
    await page.settle(2);
    assert.equal(messages(page).filter(message => message.text === RESTORED).length, 1);
});

test('a stream that goes quiet is replaced, while heartbeats keep it', async t => {
    useMockTimers(t);
    const page = await boot();
    const [source] = page.eventSources;
    source.emit('open');

    // Heartbeats every 30 s keep the stream.
    for (let period = 0; period < 3; period++) {
        await advance(t, page, 30000);
        source.emit('heartbeat', {});
    }
    assert.equal(page.eventSources.length, 1);

    // Silence for two and a half heartbeat periods: the stream was dropped without being closed.
    await advance(t, page, 70000);
    assert.equal(page.eventSources.length, 1);
    await advance(t, page, 5000);
    assert.ok(consoleLines(page, 'warn').includes('SSE: No heartbeat for 75s, replacing the stream'));
    assert.equal(source.readyState, 2);
    assert.equal(page.eventSources.length, 2);
    assert.equal(page.eventSources[1].url, '/api/events/stream?access_token=stream-token-2');
});

test('a stream stuck connecting is replaced too', async t => {
    useMockTimers(t);
    const page = await boot();
    const [source] = page.eventSources;
    await advance(t, page, 75000);
    assert.equal(source.readyState, 2);
    assert.equal(page.eventSources.length, 2);
});

test('while the stream is down the active job is polled, until the stream is back', async t => {
    useMockTimers(t);
    const { job, routes } = activeJobRoutes(1);
    const page = await boot({ routes });
    page.eventSources[0].emit('open');
    await page.settle(3);
    const polls = () => requests(page, 'GET', `/api/jobs/${JOB_ID}`).length;

    // An open stream carries the progress: no polling.
    const before = polls();
    await advance(t, page, 20000);
    assert.equal(polls(), before);

    // The stream drops and cannot be reopened for now.
    page.route(STREAM_TOKEN, 503);
    page.route(`GET /api/jobs/${JOB_ID}`, { ...job, processed_items: 3, current_file: 'Saga/Saga 003.cbz' });
    await dropStream(page);
    await advance(t, page, 15000);
    assert.ok(polls() >= before + 3, 'the job is polled every few seconds');
    assert.equal(page.$('#progressText').textContent, '3 / 4 files', 'its progress keeps moving');
    assert.ok(consoleLines(page, 'log').includes(`SSE: Stream is down, polling job ${JOB_ID}`));

    // Once the stream is open again, polling stops.
    page.route(STREAM_TOKEN, { token: 'back', expiresIn: 60 });
    await advance(t, page, 60000);
    page.eventSources.at(-1).emit('open');
    await page.settle(3);
    const afterReconnect = polls();
    await advance(t, page, 20000);
    assert.equal(polls(), afterReconnect);
});

test('reconnecting catches up on the job, the watcher status and the library missed while disconnected', async t => {
    useMockTimers(t);
    const { job, routes } = activeJobRoutes(1);
    const page = await boot({ routes });
    const watcherChecks = () => requests(page, 'GET', '/api/watcher/status').length;
    const libraryLoads = () => requests(page, 'GET', '/api/files/series').length;

    // The first connection has nothing to catch up on but the job.
    const jobPolls = requests(page, 'GET', `/api/jobs/${JOB_ID}`).length;
    const watcherBefore = watcherChecks();
    const libraryBefore = libraryLoads();
    page.eventSources[0].emit('open');
    await page.settle(3);
    t.mock.timers.tick(500);
    await page.settle(3);
    assert.equal(requests(page, 'GET', `/api/jobs/${JOB_ID}`).length, jobPolls + 1);
    assert.equal(watcherChecks(), watcherBefore);
    assert.equal(libraryLoads(), libraryBefore);

    page.route(`GET /api/jobs/${JOB_ID}`, { ...job, processed_items: 3, current_file: 'Saga/Saga 003.cbz' });
    page.route('GET /api/watcher/status', { running: false, enabled: true });
    await dropStream(page);
    t.mock.timers.tick(5000);
    await page.settle(3);
    page.eventSources.at(-1).emit('open');
    await page.settle(3);
    t.mock.timers.tick(500);
    await page.settle(3);

    assert.ok(consoleLines(page, 'log').includes(`SSE: Reconnected with active job ${JOB_ID}, fetching current status...`));
    assert.equal(page.$('#progressText').textContent, '3 / 4 files');
    assert.ok(consoleLines(page, 'log').includes('SSE: Catching up on events missed while disconnected'));
    assert.equal(watcherChecks(), watcherBefore + 1);
    assert.match(page.$('#watcherStatus').textContent, /Watcher Stopped/);
    // The overview is on screen: the library catches up when it is shown.
    assert.equal(libraryLoads(), libraryBefore, 'nothing is fetched for a library that is not on screen');
    page.run("navigate('#/library')");
    await page.settle(3);
    assert.equal(libraryLoads(), libraryBefore + 1, 'the library view is refreshed once');
});

test('coming back online or to the tab reconnects at once, but leaves an open stream alone', async t => {
    useMockTimers(t);
    const page = await boot();
    page.route(STREAM_TOKEN, 503);
    // A few failures push the next attempt far out.
    for (let attempt = 0; attempt < 6; attempt++) {
        await dropStream(page);
        t.mock.timers.tick(60000);
        await page.settle(3);
    }
    const sources = page.eventSources.length;
    page.route(STREAM_TOKEN, { token: 'online', expiresIn: 60 });

    page.window.dispatchEvent(new page.window.Event('online'));
    await page.settle(3);
    assert.ok(consoleLines(page, 'log').includes('SSE: Back online, reconnecting now'));
    assert.equal(page.eventSources.length, sources + 1);
    assert.equal(page.eventSources.at(-1).url, '/api/events/stream?access_token=online');

    page.eventSources.at(-1).emit('open');
    page.window.dispatchEvent(new page.window.Event('online'));
    page.document.dispatchEvent(new page.window.Event('visibilitychange'));
    await page.settle(3);
    assert.equal(page.eventSources.length, sources + 1, 'an open stream is kept');

    await dropStream(page);
    page.route(STREAM_TOKEN, { token: 'visible', expiresIn: 60 });
    page.document.dispatchEvent(new page.window.Event('visibilitychange'));
    await page.settle(3);
    assert.ok(consoleLines(page, 'log').includes('SSE: Page visible again, reconnecting now'));
    assert.equal(page.eventSources.at(-1).url, '/api/events/stream?access_token=visible');
});

test('a refused stream token for a signed-out session goes to the login page', async t => {
    // Signed out elsewhere, or the password, role or account changed: the stored token no longer works.
    useMockTimers(t);
    const page = await boot();
    page.route(STREAM_TOKEN, respond(401, { error: 'Unauthorized', message: 'Authentication required' }));
    await dropStream(page);
    t.mock.timers.tick(5000);
    await page.settle(3);
    assert.ok(consoleLines(page, 'warn').includes('SSE: The session has ended'));
    assert.ok(wentToLogin(page));
    await advance(t, page, 120000);
    assert.equal(page.eventSources.length, 1, 'nothing is retried');
});

test('a stream dropped after the JWT expired sends the user to the login page', async t => {
    useMockTimers(t);
    const page = await boot();
    page.window.localStorage.setItem('jwt_token', expiredToken());
    await dropStream(page);
    assert.ok(consoleLines(page, 'warn').includes('[AUTH] JWT token expired'));
    assert.ok(consoleLines(page, 'warn').includes('SSE: Authentication expired during connection'));
    assert.ok(wentToLogin(page));
    await advance(t, page, 120000);
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

test('Authelia sessions fetch the stream token with their cookies', async () => {
    const page = await bootAuthelia();
    const [call] = requests(page, 'POST', '/api/auth/stream-token');
    assert.equal(call.init.credentials, 'include');
    assert.equal(call.headers.authorization, undefined);
    assert.equal(page.eventSources[0].url, '/api/events/stream?access_token=stream-token-1');
    assert.ok(consoleLines(page, 'log').includes('SSE: Authentication mode - Authelia'));
});

for (const [description, body] of [['an empty object', {}], ['null', null]]) {
    test(`a stream token answer of ${description} is retried with backoff instead of opening a stream without a token`, async t => {
        useMockTimers(t);
        const page = await boot({ routes: { [STREAM_TOKEN]: respond(200, JSON.stringify(body), { 'Content-Type': 'application/json' }) } });
        assert.equal(page.eventSources.length, 0);
        assert.ok(consoleLines(page, 'error').includes('SSE: Failed to initialize EventSource: No stream token in the response'));
        assert.deepEqual(retryDelays(page), ['1@5s']);

        page.route(STREAM_TOKEN, { token: 'second try', expiresIn: 60 });
        t.mock.timers.tick(5000);
        await page.settle(3);
        assert.equal(page.eventSources.length, 1);
        assert.equal(page.eventSources[0].url, '/api/events/stream?access_token=second%20try');
    });
}

test('starting the stream again while it is being opened does not open a second one', async () => {
    const gate = deferred();
    const page = await boot({ routes: { [STREAM_TOKEN]: () => gate.promise } });
    assert.equal(requests(page, 'POST', '/api/auth/stream-token').length, 1);

    page.run('initEventSource()');
    await page.settle(3);
    assert.equal(requests(page, 'POST', '/api/auth/stream-token').length, 1);

    gate.resolve({ token: 'only-one', expiresIn: 60 });
    await page.settle(3);
    assert.equal(page.eventSources.length, 1);
    assert.equal(page.eventSources[0].url, '/api/events/stream?access_token=only-one');
});

test('a refused Authelia stream token is retried with backoff until the server issues one', async t => {
    useMockTimers(t);
    const page = await bootAuthelia({ [STREAM_TOKEN]: respond(401, { error: 'Unauthorized' }) });
    assert.equal(page.eventSources.length, 0);
    assert.ok(consoleLines(page, 'error').includes('SSE: Failed to get a stream token - status: 401'));
    assert.ok(consoleLines(page, 'error').includes('SSE: Failed to initialize EventSource: No stream token (401)'));
    assert.deepEqual(retryDelays(page), ['1@5s']);

    page.route(STREAM_TOKEN, { token: 'second try', expiresIn: 60 });
    t.mock.timers.tick(5000);
    await page.settle(3);
    assert.equal(page.eventSources.length, 1);
    assert.equal(page.eventSources[0].url, '/api/events/stream?access_token=second%20try');
    assert.equal(wentToLogin(page), false);
});

// A proxy sending an expired session to the Authelia portal, as a fetch with redirect: 'manual' sees it.
function opaqueRedirect() {
    const response = new Response(null, { status: 200 });
    Object.defineProperties(response, {
        type: { value: 'opaqueredirect' },
        status: { value: 0 },
        ok: { value: false }
    });
    return response;
}

for (const [description, status] of [
    ['reports the Authelia session as ended', { autheliaEnabled: true, isAuthenticated: false }],
    ['reports that Authelia is no longer in use', { autheliaEnabled: false, isAuthenticated: true }],
    ['answers 401', 401],
    ['answers 403', 403],
    ['is redirected to the Authelia portal', () => opaqueRedirect()]
]) {
    test(`a dropped Authelia stream goes to login when the session check ${description}`, async () => {
        const page = await bootAuthelia();
        page.route('GET /api/auth/status', status);
        await dropStream(page);
        assert.ok(consoleLines(page, 'warn').includes('[AUTH] Authelia session expired or invalid'));
        assert.ok(wentToLogin(page));
    });
}

// An outage says nothing about the session: a wifi change or a container restart must not sign anybody out.
for (const [description, status, expectedLog] of [
    ['fails', 503, '[AUTH] Could not check the Authelia session - status: 503'],
    ['fails at the proxy', 502, '[AUTH] Could not check the Authelia session - status: 502'],
    ['is unreachable', () => { throw new TypeError('Failed to fetch'); }, '[AUTH] Could not check the session:'],
    ['answers with something other than JSON', respond(200, '<html>Starting up</html>', { 'Content-Type': 'text/html' }), '[AUTH] Could not check the session:']
]) {
    test(`a dropped Authelia stream keeps retrying when the session check ${description}, until the server is back`, async t => {
        useMockTimers(t);
        const page = await bootAuthelia();
        page.route('GET /api/auth/status', status);

        await dropStream(page);
        assert.ok(consoleLines(page, 'warn').some(line => line.startsWith(expectedLog)), `logged: ${consoleLines(page, 'warn').join(' | ')}`);
        assert.equal(wentToLogin(page), false);
        assert.deepEqual(retryDelays(page), ['1@5s']);

        // The retry finds the outage still going, and backs off.
        t.mock.timers.tick(5000);
        await page.settle(3);
        assert.equal(wentToLogin(page), false);
        assert.equal(page.eventSources.length, 1);
        assert.deepEqual(retryDelays(page), ['1@5s', '2@7.5s']);

        page.route('GET /api/auth/status', AUTHELIA_SESSION);
        t.mock.timers.tick(7500);
        await page.settle(3);
        assert.equal(page.eventSources.length, 2);
        assert.equal(wentToLogin(page), false);
    });
}

test('during an Authelia outage the active job is still polled, and the stream returns when the browser is online again', async t => {
    useMockTimers(t);
    const { routes } = activeJobRoutes();
    const page = await bootAuthelia(routes);
    await page.settle(3);
    const polls = () => requests(page, 'GET', `/api/jobs/${JOB_ID}`).length;
    page.route('GET /api/auth/status', () => { throw new TypeError('Failed to fetch'); });
    await dropStream(page);

    const before = polls();
    await advance(t, page, 15000);
    assert.ok(polls() >= before + 2, 'the job keeps being polled during the outage');
    assert.equal(wentToLogin(page), false);
    const streams = page.eventSources.length;

    page.route('GET /api/auth/status', AUTHELIA_SESSION);
    page.window.dispatchEvent(new page.window.Event('online'));
    await page.settle(3);
    assert.equal(page.eventSources.length, streams + 1);
    assert.equal(wentToLogin(page), false);
});

test('the Authelia session check does not follow redirects, which would look like being offline', async () => {
    const page = await bootAuthelia();
    await dropStream(page);
    const check = requests(page, 'GET', '/api/auth/status').at(-1);
    assert.equal(check.init.redirect, 'manual');
    assert.equal(check.init.credentials, 'include');
});

test('a dropped Authelia stream with a live session is reconnected with a new stream token', async t => {
    useMockTimers(t);
    const page = await bootAuthelia();
    await dropStream(page);
    assert.equal(wentToLogin(page), false);
    t.mock.timers.tick(5000);
    await page.settle(3);
    assert.equal(page.eventSources.length, 2);
    assert.equal(requests(page, 'POST', '/api/auth/stream-token').length, 2);
});

test('leaving the page closes the stream and stops reconnecting, but asking whether to leave does not', async t => {
    useMockTimers(t);
    const page = await boot();
    const [source] = page.eventSources;
    const unload = new page.window.Event('beforeunload', { cancelable: true });
    page.window.dispatchEvent(unload);
    assert.equal(unload.defaultPrevented, false, 'no job is running, so leaving is not questioned');
    assert.notEqual(source.readyState, 2, 'the user may still stay on the page');

    await dropStream(page);
    page.window.dispatchEvent(new page.window.Event('pagehide'));
    await advance(t, page, 120000);
    assert.equal(page.eventSources.length, 1, 'the pending reconnect and the watchdog are cancelled');

    const second = await boot();
    const [open] = second.eventSources;
    second.window.dispatchEvent(new second.window.Event('pagehide'));
    assert.equal(open.readyState, 2);
    assert.doesNotThrow(() => second.run('cleanupEventSource()'));
});

test('a page restored from the back/forward cache reopens its stream', async () => {
    const page = await boot();
    const [source] = page.eventSources;
    const hide = new page.window.Event('pagehide');
    Object.defineProperty(hide, 'persisted', { value: true });
    page.window.dispatchEvent(hide);
    assert.equal(source.readyState, 2);

    const show = new page.window.Event('pageshow');
    Object.defineProperty(show, 'persisted', { value: true });
    page.window.dispatchEvent(show);
    await page.settle(3);
    assert.equal(page.eventSources.length, 2);
    assert.equal(page.eventSources[1].url, '/api/events/stream?access_token=stream-token-2');
});

test('a connection attempt interrupted by cleanup does not open a stream afterwards', async () => {
    const page = await boot();
    const pending = deferred();
    page.route(STREAM_TOKEN, () => pending.promise);
    const attempt = page.run('initEventSource()');
    await page.settle(2);
    page.run('cleanupEventSource()');
    pending.resolve({ token: 'late', expiresIn: 60 });
    await attempt;
    await page.settle(2);
    assert.equal(page.eventSources.length, 1);
    assert.equal(page.eventSources[0].readyState, 2);
});

test('malformed stream messages are logged and ignored', async () => {
    const page = await boot();
    page.eventSources[0].emit('message', '{not json');
    assert.ok(consoleLines(page, 'error').some(line => line.startsWith('SSE: Error parsing event data:')));
    page.eventSources[0].emit('message', { type: 'library_exploded', data: {} });
    assert.ok(consoleLines(page, 'log').includes('SSE: Unknown event type: library_exploded'));
});
