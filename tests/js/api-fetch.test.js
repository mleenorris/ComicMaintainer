'use strict';

// js/app/api.js: apiFetch(), the one way the app talks to its API, and the
// debug logging that replaced the per-request console output.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { WWWROOT } = require('./support/sandbox');
const { loadPage, plain, respond } = require('./support/dom');
const { jwt, validToken } = require('./support/app');
const { boot, consoleLines, messages, requests, wentToLogin } = require('./support/core-helpers');

const expiredToken = () => jwt({ sub: 'alice', exp: Math.floor(Date.now() / 1000) - 60 });

// index.html's markup with only the API plumbing loaded.
function apiPage(options = {}) {
    return loadPage({
        scripts: ['js/shared.js', 'js/app/api.js'],
        localStorage: { jwt_token: validToken() },
        ...options
    });
}

/** What the apiFetch() call in `code` rejected with. */
async function failure(page, code = "apiFetch('/api/thing')") {
    try {
        await page.run(code);
    } catch (error) {
        return error;
    }
    return assert.fail(`${code} did not fail`);
}

test('a request carries the session and its answer comes back parsed', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': { value: 7 } } });

    assert.deepEqual(plain(await page.run("apiFetch('/api/thing')")), { value: 7 });

    const [call] = requests(page, 'GET', '/api/thing');
    assert.equal(call.init.credentials, 'include');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.equal(call.headers['x-client-session'], page.window.localStorage.getItem('client_cache_session_id'));
});

test('an Authelia session relies on its cookie', async () => {
    const page = apiPage({
        localStorage: { authelia_authenticated: 'true', jwt_token: validToken() },
        routes: { 'GET /api/thing': {} }
    });

    await page.run("apiFetch('/api/thing')");

    const [call] = requests(page, 'GET', '/api/thing');
    assert.equal(call.init.credentials, 'include');
    assert.equal(call.headers.authorization, undefined);
});

test('an object body is sent as JSON', async () => {
    const page = apiPage({ routes: { 'POST /api/thing': {} } });

    await page.run("apiFetch('/api/thing', { method: 'POST', body: { files: ['a.cbz'] } })");

    const [call] = requests(page, 'POST', '/api/thing');
    assert.deepEqual(call.body, { files: ['a.cbz'] });
    assert.equal(call.headers['content-type'], 'application/json');
});

test('an object body sent without the session still says it is JSON', async () => {
    const page = apiPage({ routes: { 'POST /api/thing': {} } });

    await page.run("apiFetch('/api/thing', { method: 'POST', body: { a: 1 }, auth: false })");

    assert.equal(requests(page, 'POST', '/api/thing')[0].headers['content-type'], 'application/json');
});

test('a string body is sent as it is', async () => {
    const page = apiPage({ routes: { 'POST /api/thing': {} } });

    await page.run("apiFetch('/api/thing', { method: 'POST', body: 'raw text' })");

    assert.equal(requests(page, 'POST', '/api/thing')[0].init.body, 'raw text');
});

test('a Blob body is sent as it is', async () => {
    const page = apiPage({ routes: { 'POST /api/thing': {} } });

    await page.run("apiFetch('/api/thing', { method: 'POST', body: new Blob(['x']) })");

    assert.ok(requests(page, 'POST', '/api/thing')[0].init.body instanceof page.window.Blob);
});

test('a form is sent as multipart, leaving the boundary to the browser', async () => {
    const page = apiPage({ routes: { 'PUT /api/thing': {} } });

    await page.run("const form = new FormData(); form.append('file', 'x'); apiFetch('/api/thing', { method: 'PUT', body: form })");

    const [call] = requests(page, 'PUT', '/api/thing');
    assert.ok(call.init.body instanceof page.window.FormData);
    assert.equal(call.headers['content-type'], undefined);
    assert.match(call.headers.authorization, /^Bearer /);
});

test('no body means none is sent, and fetch options pass through', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': {} } });

    await page.run("apiFetch('/api/thing', { body: null, keepalive: true, headers: { 'X-Extra': '1' } })");

    const [call] = requests(page, 'GET', '/api/thing');
    assert.equal('body' in call.init, false);
    assert.equal(call.init.keepalive, true);
    assert.equal(call.headers['x-extra'], '1');
    assert.match(call.headers.authorization, /^Bearer /);
});

test('an empty answer parses to an empty object', async () => {
    for (const answer of [undefined, 204, respond(200, '')]) {
        const page = apiPage({ routes: { 'GET /api/thing': answer } });
        assert.deepEqual(plain(await page.run("apiFetch('/api/thing')")), {});
    }
});

test('a plain-text success has nothing to parse and counts as empty', async () => {
    const page = apiPage({ routes: { 'POST /api/thing': respond(200, 'Deleted') } });

    assert.deepEqual(plain(await page.run("apiFetch('/api/thing', { method: 'POST' })")), {});
});

test('an HTML page instead of JSON is a failure, not an empty result', async () => {
    const proxyPage = respond(200, '<html>Sign in</html>', { 'Content-Type': 'text/html' });
    const page = apiPage({ routes: { 'GET /api/thing': proxyPage, 'GET /api/named': proxyPage } });

    const error = await failure(page);
    assert.equal(error.message, 'The server sent an unexpected response.');
    assert.equal(error.status, 200);

    const named = await failure(page, "apiFetch('/api/named', { errorMessage: 'Could not load it' })");
    assert.equal(named.message, 'Could not load it');
});

test('json: false hands the caller the response, whatever its status', async () => {
    const page = apiPage({ routes: { 'GET /api/image': respond(404, { error: 'gone' }), 'GET /api/forbidden': 403 } });

    assert.equal(await page.run("apiFetch('/api/image', { json: false }).then(r => r.status)"), 404);
    assert.equal(await page.run("apiFetch('/api/forbidden', { json: false }).then(r => r.status)"), 403);
    assert.equal(wentToLogin(page), false);
});

// ── Failures ──────────────────────────────────────────────────────────────

const FAILURES = [
    ['the error field', respond(400, { error: 'Bad series name' }), 'Bad series name'],
    ['ProblemDetails with an error field', respond(500, { title: 'Oops', detail: 'Details', error: 'Could not save' }), 'Could not save'],
    ['ProblemDetails detail', respond(409, { title: 'Conflict', detail: 'Already running' }), 'Already running'],
    ['ProblemDetails title', respond(404, { title: 'Not Found', status: 404 }), 'Not Found'],
    ['a message field', respond(503, { message: 'Starting up' }), 'Starting up'],
    ['a JSON string', respond(400, '"Language must be two letters"', { 'Content-Type': 'application/json' }), 'Language must be two letters'],
    ['a plain-text 4xx', respond(400, 'Files are required'), 'Files are required'],
    ['a 4xx with blanks around it', respond(400, '  Trimmed  \n'), 'Trimmed'],
    ['blank fields', respond(400, { error: '  ', detail: 3, title: 'Used' }), 'Used'],
    ['a 5xx text page from a proxy', respond(502, 'Bad Gateway'), 'HTTP error! status: 502'],
    ['a 4xx HTML page', respond(404, '<html>Nope</html>', { 'Content-Type': 'text/html' }), 'HTTP error! status: 404'],
    ['JSON without a message', respond(500, { code: 7 }), 'HTTP error! status: 500'],
    ['an empty body', 500, 'HTTP error! status: 500']
];

for (const [name, answer, message] of FAILURES) {
    test(`a failure reads ${name}`, async () => {
        const page = apiPage({ routes: { 'GET /api/thing': answer } });

        const error = await failure(page);

        assert.equal(error.message, message);
        assert.equal(error.fromServer, !message.startsWith('HTTP error!'));
    });
}

test('errorMessage replaces the generic text, but not the server\'s own', async () => {
    const page = apiPage({ routes: { 'POST /api/mute': 500, 'POST /api/told': respond(500, { error: 'Locked' }) } });
    const start = path => `apiFetch('${path}', { method: 'POST', errorMessage: 'Failed to start job' })`;

    assert.equal((await failure(page, start('/api/mute'))).message, 'Failed to start job');
    assert.equal((await failure(page, start('/api/told'))).message, 'Locked');
});

test('a failure carries its status and correlation id', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': respond(500, { error: 'boom' }, { 'X-Correlation-Id': 'corr-1' }) } });

    const error = await failure(page);

    assert.equal(error.status, 500);
    assert.equal(error.correlationId, 'corr-1');
});

test('a GlobalExceptionHandler problem document reaches the caller as its reason and reference', async () => {
    // The document ControllerFailureResponseTests pins on the server's side.
    const problem = {
        type: 'https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1',
        title: 'An unexpected error occurred.',
        status: 500,
        detail: 'Error retrieving processing history',
        instance: '/api/processing-history',
        correlationId: 'corr-9',
        error: 'Error retrieving processing history'
    };
    const page = apiPage({
        routes: { 'GET /api/thing': respond(500, problem, { 'Content-Type': 'application/problem+json', 'X-Correlation-Id': 'corr-9' }) }
    });

    const error = await failure(page);

    assert.equal(error.message, 'Error retrieving processing history');
    assert.equal(error.status, 500);
    assert.equal(error.correlationId, 'corr-9');
    assert.equal(error.fromServer, true);
    assert.equal(page.window.withApiFailureReference('Could not load: ' + error.message, error), 'Could not load: Error retrieving processing history (ref: corr-9)');
});

test('a failed request that never got an answer is rejected as it is', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': () => { throw new TypeError('Failed to fetch'); } } });

    assert.equal((await failure(page)).message, 'Failed to fetch');
});

test('a body that cannot be read leaves the generic message', async () => {
    const page = apiPage({
        routes: {
            'GET /api/thing': () => {
                const response = new Response('body', { status: 500 });
                response.text = () => Promise.reject(new TypeError('aborted'));
                return response;
            }
        }
    });

    assert.equal((await failure(page)).message, 'HTTP error! status: 500');
});

test('a 403 says the user may not do that, with no redirect and no reference', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': respond(403, undefined, { 'X-Correlation-Id': 'corr-2' }) } });

    const error = await failure(page);

    assert.equal(error.message, 'You do not have permission to do that.');
    assert.equal(error.status, 403);
    assert.equal(error.correlationId, null);
    assert.equal(error.authHandled, undefined);
    assert.equal(wentToLogin(page), false);
});

test('a 403 keeps the server\'s reason, or the caller\'s wording', async () => {
    const page = apiPage({ routes: { 'GET /api/told': respond(403, { error: 'Admins only' }), 'GET /api/mute': 403 } });

    assert.equal((await failure(page, "apiFetch('/api/told')")).message, 'Admins only');
    assert.equal((await failure(page, "apiFetch('/api/mute', { forbiddenMessage: 'No access' })")).message, 'No access');
});

test('a 403 with only the status word leaves the wording to the app', async () => {
    const refusals = {
        // What the sign-in layer really sends (JwtSessionEvents.WriteForbiddenAsync).
        'GET /api/signed-in': respond(403, { error: 'Forbidden', message: 'Insufficient permissions' }),
        // ASP.NET's default ProblemDetails, and a proxy's plain text.
        'GET /api/problem': respond(403, { title: 'Forbidden', status: 403 }),
        'GET /api/proxy': respond(403, 'Forbidden', { 'Content-Type': 'text/plain' })
    };
    const page = apiPage({ routes: refusals });

    for (const route of Object.keys(refusals).map(key => key.split(' ')[1])) {
        const error = await failure(page, `apiFetch('${route}')`);
        assert.equal(error.message, 'You do not have permission to do that.', route);
        assert.equal(error.status, 403, route);
        assert.equal(error.fromServer, false, route);
        assert.equal((await failure(page, `apiFetch('${route}', { forbiddenMessage: 'No access' })`)).message, 'No access', route);
    }
});

// ── The end of a session ──────────────────────────────────────────────────

test('a 401 ends the session and is flagged as handled', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': 401 } });

    const error = await failure(page);

    assert.equal(error.status, 401);
    assert.equal(error.authHandled, true);
    assert.equal(error.correlationId, null);
    assert.ok(wentToLogin(page));
    assert.equal(page.window.localStorage.getItem('jwt_token'), null);
});

test('a 401 ends the session for a caller that handles statuses itself too', async () => {
    const page = apiPage({ routes: { 'GET /api/image': 401 } });

    const error = await failure(page, "apiFetch('/api/image', { json: false })");

    assert.equal(error.authHandled, true);
    assert.ok(wentToLogin(page));
});

test('redirectOn401: false leaves a 401 to the caller and keeps the session headers', async () => {
    const page = apiPage({ routes: { 'POST /api/token': 401 } });

    const status = await page.run("apiFetch('/api/token', { method: 'POST', json: false, redirectOn401: false }).then(r => r.status)");

    assert.equal(status, 401);
    assert.match(requests(page, 'POST', '/api/token')[0].headers.authorization, /^Bearer /);
    assert.equal(wentToLogin(page), false);
});

test('auth: false sends no session and leaves a 401 to the caller', async () => {
    const page = apiPage({ routes: { 'GET /api/status': 401 } });

    const status = await page.run("apiFetch('/api/status', { auth: false, json: false }).then(r => r.status)");

    assert.equal(status, 401);
    const [call] = requests(page, 'GET', '/api/status');
    assert.equal(call.headers.authorization, undefined);
    assert.equal(call.init.credentials, 'include');
    assert.equal(wentToLogin(page), false);
});

test('an expired token goes to the login page without sending the request', async () => {
    const page = apiPage({ localStorage: { jwt_token: expiredToken() } });

    const error = await failure(page);

    assert.equal(error.authHandled, true);
    assert.deepEqual(page.fetchCalls, []);
    assert.ok(wentToLogin(page));
});

test('allowExpiredToken sends the request and lets the answer decide', async () => {
    const token = expiredToken();
    const page = apiPage({ localStorage: { jwt_token: token }, routes: { 'GET /api/thing': { ok: true } } });

    assert.deepEqual(plain(await page.run("apiFetch('/api/thing', { allowExpiredToken: true })")), { ok: true });

    assert.equal(page.fetchCalls[0].headers.authorization, `Bearer ${token}`);
    assert.equal(wentToLogin(page), false);
    assert.equal(page.window.localStorage.getItem('jwt_token'), token);

    page.route('GET /api/thing', 401);
    const error = await failure(page, "apiFetch('/api/thing', { allowExpiredToken: true })");

    assert.equal(error.authHandled, true);
    assert.ok(wentToLogin(page));
});

test('failures on the way to the login page are not shown', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': 401 } });
    await failure(page);

    page.run("showMessage('Failed to load series: Authentication required', 'error')");
    page.run("showMessage('Could not check job status', 'warning')");
    page.run("showMessage('Saved', 'success')");

    assert.deepEqual(messages(page).map(message => message.type), ['success']);
});

test('a message is shown again once a new session has signed in', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': 401 } });
    await failure(page);

    page.window.localStorage.setItem('jwt_token', validToken());
    page.run("showMessage('Failed to save', 'error')");

    assert.deepEqual(messages(page).map(message => message.text), ['Failed to save']);
});

test('the end of a session is not reported as a client error', async () => {
    const page = await boot({ routes: { 'POST /api/client-errors': 202 } });

    const event = new page.window.Event('unhandledrejection');
    event.reason = Object.assign(new page.window.Error('Authentication required'), { authHandled: true });
    page.window.dispatchEvent(event);
    await page.settle();

    assert.deepEqual(requests(page, 'POST', '/api/client-errors'), []);
    assert.deepEqual(consoleLines(page, 'error').filter(line => line.startsWith('Unhandled error')), []);
});

// ── Logging ───────────────────────────────────────────────────────────────

test('requests are not logged unless the debug flag is set', async () => {
    const page = apiPage({ routes: { 'GET /api/thing': {} } });

    await page.run("apiFetch('/api/thing')");

    assert.deepEqual(consoleLines(page, 'debug'), []);
    assert.deepEqual(consoleLines(page, 'log'), []);
});

test('with the debug flag set, each request is logged', async () => {
    const page = apiPage({
        localStorage: { jwt_token: validToken(), debug_api: 'true' },
        routes: { 'GET /api/thing': {} }
    });

    await page.run("apiFetch('/api/thing')");

    const lines = consoleLines(page, 'debug');
    assert.ok(lines.includes('[API] GET /api/thing'), lines.join('\n'));
    assert.ok(lines.includes('[API] Adding authorization header (token present)'), lines.join('\n'));
});

test('an Authelia session is logged under the debug flag too', () => {
    const page = apiPage({ localStorage: { authelia_authenticated: 'true', debug_api: 'true' } });

    page.run('getAuthHeaders()');

    assert.ok(consoleLines(page, 'debug').includes('[API] Using Authelia authentication (cookies)'));
});

test('debug logging stays off when storage cannot be read', () => {
    const page = apiPage();
    // On the prototype: assigning to the storage object itself would only store
    // an item named "getItem".
    page.run("Storage.prototype.getItem = () => { throw new Error('denied'); }");

    assert.equal(page.run('isApiDebugEnabled()'), false);
});

// sw.js stays out of this: it runs in the service worker, which has no session
// of its own and no page to redirect, and it does not live under js/.
test('every page script makes its requests through apiFetch', () => {
    const scripts = fs.readdirSync(path.join(WWWROOT, 'js'), { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile() && entry.name.endsWith('.js'))
        .map(entry => path.relative(WWWROOT, path.join(entry.parentPath, entry.name)).split(path.sep).join('/'))
        .filter(script => script !== 'js/app/api.js');
    assert.ok(scripts.includes('js/pages/login.js') && scripts.includes('js/pages/setup.js'));

    for (const script of scripts) {
        const direct = fs.readFileSync(path.join(WWWROOT, script), 'utf8').split(/\r?\n/)
            .filter(line => !/^\s*(?:\/\/|\*)/.test(line) && /\bfetch\(/.test(line));
        assert.deepEqual(direct, [], script);
    }
});

for (const [page, url] of [['login.html', '/api/auth/login'], ['setup.html', '/api/auth/setup']]) {
    test(`${page} posts without a session and leaves a refusal to the page`, async () => {
        const form = page === 'login.html' ? '#username' : '#setupToken';
        const loaded = loadPage({
            html: page,
            url: `http://localhost/${page}`,
            routes: {
                'GET /api/auth/setup-required': { setupRequired: page === 'setup.html' },
                'GET /api/auth/status': { autheliaEnabled: false, isAuthenticated: false },
                [`POST ${url}`]: respond(401, { error: 'Nope' })
            }
        });
        await loaded.settle();
        assert.ok(loaded.$(form), 'the page is ready');

        await loaded.run(`apiFetch('${url}', { method: 'POST', auth: false, json: false, body: { a: 1 } })`);
        const [call] = requests(loaded, 'POST', url);

        assert.equal(call.init.credentials, 'include');
        assert.equal(call.init.headers['Content-Type'], 'application/json');
        assert.equal(call.init.headers.Authorization, undefined);
        assert.deepEqual(loaded.navigations, [], 'a 401 here is a wrong password, not an ended session');
    });
}
