'use strict';

// Self-tests for the jsdom page harness (support/dom.js): the full SPA boots
// against the real index.html, and the stubs other tests rely on behave.

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPage, respond, TEST_APP_VERSION } = require('./support/dom');

function jwt(payload) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature`;
}

const validToken = () => jwt({ sub: 'alice', exp: Math.floor(Date.now() / 1000) + 3600 });

test('an unauthenticated visitor is sent to the login page', async () => {
    const page = loadPage({
        localStorage: { username: 'stale' },
        routes: { 'GET /api/auth/status': { autheliaEnabled: false, isAuthenticated: false } }
    });
    await page.settle();

    assert.deepEqual(page.navigations.map(n => n.url), ['http://localhost/login.html']);
    // The redirect is recorded rather than attempted, so jsdom reports nothing.
    assert.deepEqual(page.errors, []);
    assert.equal(page.window.location.href, 'http://localhost/index.html');
    assert.equal(page.window.localStorage.getItem('username'), null);
    // Nothing past the auth check runs.
    assert.deepEqual(page.fetchCalls.filter(c => c.path !== '/api/auth/status' && c.path !== '/api/preferences'), []);
});

test('fragment navigations stay in the document and fire hashchange; reloads are recorded', async () => {
    const page = loadPage({ markup: '<!DOCTYPE html><html><body></body></html>', url: 'http://localhost/page.html?q=1' });
    await page.settle();
    const hashes = [];
    page.window.addEventListener('hashchange', () => hashes.push(page.window.location.hash));

    page.window.location.hash = '#settings';
    page.window.location.replace('/elsewhere.html');
    page.window.location.reload();
    await page.waitFor(() => hashes.length === 1);

    assert.deepEqual(hashes, ['#settings']);
    assert.equal(page.window.location.href, 'http://localhost/page.html?q=1#settings');
    assert.deepEqual(page.navigations, [
        { url: 'http://localhost/page.html?q=1#settings', replace: false },
        { url: 'http://localhost/elsewhere.html', replace: true },
        { url: 'http://localhost/page.html?q=1#settings', reload: true }
    ]);
    assert.deepEqual(page.errors, []);
});

test('the SPA boots against the real index.html with a valid session', async () => {
    const page = loadPage({
        localStorage: { jwt_token: validToken(), username: 'alice' },
        routes: {
            'GET /api/auth/status': { autheliaEnabled: false, isAuthenticated: false },
            'GET /api/version': { version: '9.9.9' }
        }
    });
    await page.settle();

    assert.equal(page.run('APP_VERSION'), TEST_APP_VERSION);
    assert.equal(page.window.location.hash, '#/home');
    assert.equal(page.$('#sidebarUsername').textContent, 'alice');
    assert.ok(page.fetchCalls.some(c => c.path === '/api/files/series'));
    assert.equal(page.fetchCalls.find(c => c.path === '/api/version').headers.authorization.startsWith('Bearer '), true);
    assert.equal(page.eventSources.length, 1);
    assert.deepEqual(page.serviceWorkerRegistrations.map(r => r.url), ['/sw.js']);
});

test('fetch routes match by method, path or pattern and record unmatched calls', async () => {
    const page = loadPage({ scripts: [], inlineScripts: false });
    page.route('GET /api/a', { a: 1 });
    page.route('/api/b', call => respond(201, { method: call.method, q: call.query.x }));
    page.route(/^DELETE \/api\/items\/\d+$/, 204);
    page.route('GET /api/a', 'newest wins');

    const fetch = page.window.fetch;
    assert.equal(await (await fetch('/api/a')).text(), 'newest wins');
    const b = await fetch('/api/b?x=2', { method: 'POST', body: JSON.stringify({ y: 1 }) });
    assert.equal(b.status, 201);
    assert.deepEqual(await b.json(), { method: 'POST', q: '2' });
    assert.deepEqual(page.fetchCalls[1].body, { y: 1 });
    assert.equal((await fetch('/api/items/7', { method: 'DELETE' })).status, 204);
    assert.equal((await fetch('/api/missing')).status, 404);
    assert.deepEqual(page.unmatchedRequests.map(c => c.url), ['/api/missing']);

    const controller = new page.window.AbortController();
    page.route('/api/slow', () => new Promise(() => {}));
    const pending = fetch('/api/slow', { signal: controller.signal });
    controller.abort();
    await assert.rejects(pending, { name: 'AbortError' });
});

test('browser stubs record what the page does', async () => {
    const page = loadPage({ scripts: [], inlineScripts: false, media: { '(max-width: 600px)': true }, confirm: false });
    const { window } = page;

    const mql = window.matchMedia('(max-width: 600px)');
    assert.equal(mql.matches, true);
    let seen = null;
    mql.addEventListener('change', event => { seen = event.matches; });
    mql.change(false);
    assert.equal(seen, false);

    assert.equal(window.confirm('Sure?'), false);
    assert.deepEqual(page.dialogs, [{ kind: 'confirm', message: 'Sure?' }]);

    await window.navigator.clipboard.writeText('copied');
    assert.deepEqual(page.clipboard, ['copied']);

    const source = new window.EventSource('/api/events');
    let received = null;
    source.addEventListener('job', event => { received = JSON.parse(event.data); });
    source.emit('job', { id: 1 });
    assert.deepEqual(received, { id: 1 });

    const entries = [];
    const observer = new window.IntersectionObserver(found => entries.push(...found));
    observer.observe(page.document.body);
    observer.trigger(true);
    assert.equal(entries[0].target, page.document.body);
    assert.equal(entries[0].isIntersecting, true);

    window.scrollTo(0, 120);
    assert.equal(window.scrollY, 120);
    page.document.body.scrollIntoView();
    assert.deepEqual(page.scrolledIntoView, [page.document.body]);
});

test('inline onclick handlers in the markup call page globals', () => {
    const page = loadPage({
        markup: '<!doctype html><body><button id="go" onclick="clicked(this.id)">Go</button></body>',
        scripts: [{ code: 'var calls = []; function clicked(id) { calls.push(id); }' }]
    });
    page.click('#go');
    assert.deepEqual(page.evaluate('calls'), ['go']);
    page.close();
});

test('mock timers drive page timers while settle() keeps working', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = loadPage({
        markup: '<!doctype html><body></body>',
        scripts: [{ code: 'var ticks = 0; setInterval(() => ticks++, 60000); setTimeout(() => { window.late = true; }, 5000);' }]
    });
    await page.settle();
    assert.equal(page.run('ticks'), 0);
    t.mock.timers.tick(60000);
    assert.equal(page.run('ticks'), 1);
    assert.equal(page.run('window.late'), true);
    // jsdom re-arms intervals as chained timeouts, so advance one period at a time.
    t.mock.timers.tick(60000);
    t.mock.timers.tick(60000);
    assert.equal(page.run('ticks'), 3);
});
