'use strict';

// js/app/error-reporting.js and the window listeners js/main.js registers
// for it: uncaught errors / unhandled rejections are posted to
// /api/client-errors (rate limited, skipped offline) and surfaced to the user.

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadScripts } = require('./support/sandbox');
const { boot, consoleLines, messages, requests } = require('./support/core-helpers');

const BANNER = 'Something went wrong. If the page is not behaving as expected, reload it.';

function bootReporting(options = {}) {
    return boot({ ...options, routes: { 'POST /api/client-errors': 202, ...options.routes } });
}

function throwInPage(page, code, filename = 'http://localhost/js/app/menus.js?v=test-version') {
    const error = page.run(code);
    page.window.dispatchEvent(new page.window.ErrorEvent('error', { error, message: error.message, filename }));
    return error;
}

function rejectInPage(page, reason) {
    const event = new page.window.Event('unhandledrejection');
    event.reason = reason;
    page.window.dispatchEvent(event);
}

const reports = page => requests(page, 'POST', '/api/client-errors');

test('an uncaught error is posted to the server and the user is told to reload', async () => {
    const page = await bootReporting();
    assert.equal(reports(page).length, 0, 'the boot itself reports nothing');
    const error = throwInPage(page, "new TypeError('menu is undefined')");
    await page.settle(2);

    const [report] = reports(page);
    assert.deepEqual(report.body, {
        name: 'TypeError',
        message: 'menu is undefined',
        stack: error.stack,
        source: 'http://localhost/js/app/menus.js?v=test-version',
        url: '/index.html',
        kind: 'Error'
    });
    assert.equal(report.init.keepalive, true);
    assert.equal(report.init.credentials, 'include');
    assert.equal(report.headers['content-type'], 'application/json');
    assert.equal(report.headers.authorization, `Bearer ${page.window.localStorage.getItem('jwt_token')}`);
    assert.deepEqual(messages(page), [{ type: 'error', text: BANNER, role: 'alert' }]);
    assert.ok(consoleLines(page, 'error').includes('Unhandled error: menu is undefined'));
});

test('an unhandled rejection with an error-like reason keeps its name and stack', async () => {
    const page = await bootReporting();
    rejectInPage(page, { name: 'ApiError', message: 'Series not found', stack: 'ApiError: Series not found\n    at loadSeries' });
    await page.settle(2);
    assert.deepEqual(reports(page)[0].body, {
        name: 'ApiError',
        message: 'Series not found',
        stack: 'ApiError: Series not found\n    at loadSeries',
        source: '',
        url: '/index.html',
        kind: 'UnhandledRejection'
    });
    assert.deepEqual(messages(page).map(message => message.text), [BANNER]);
});

test('error events without an Error object, such as failed resource loads, are ignored', async () => {
    const page = await bootReporting();
    page.window.dispatchEvent(new page.window.ErrorEvent('error', { message: 'Script error.' }));
    await page.settle(2);
    assert.equal(reports(page).length, 0);
    assert.deepEqual(messages(page), []);
    assert.equal(consoleLines(page, 'error').some(line => line.startsWith('Unhandled error:')), false);
});

test('reports are rate limited to one every ten seconds, but every failure is logged', async () => {
    const page = await bootReporting();
    page.run('Date.now = () => 1700000000000');
    throwInPage(page, "new Error('first')");
    page.run('Date.now = () => 1700000009999');
    throwInPage(page, "new Error('second')");
    rejectInPage(page, 'third');
    await page.settle(2);
    assert.deepEqual(reports(page).map(report => report.body.message), ['first']);
    assert.equal(messages(page).length, 1);

    page.run('Date.now = () => 1700000010000');
    throwInPage(page, "new Error('fourth')");
    await page.settle(2);
    assert.deepEqual(reports(page).map(report => report.body.message), ['first', 'fourth']);
    assert.equal(messages(page).length, 2);
    const logged = consoleLines(page, 'error').filter(line => line.startsWith('Unhandled error:'));
    assert.deepEqual(logged, ['Unhandled error: first', 'Unhandled error: second', 'Unhandled error: third', 'Unhandled error: fourth']);
});

test('nothing is reported or shown while the browser is offline', async () => {
    const page = await bootReporting();
    Object.defineProperty(page.window.navigator, 'onLine', { value: false, configurable: true });
    throwInPage(page, "new Error('fetch failed while offline')");
    await page.settle(2);
    assert.equal(reports(page).length, 0);
    assert.deepEqual(messages(page), []);
    assert.ok(consoleLines(page, 'error').includes('Unhandled error: fetch failed while offline'));
});

test('a report that cannot be delivered is dropped silently', async () => {
    const page = await bootReporting({
        routes: { 'POST /api/client-errors': () => { throw new TypeError('Failed to fetch'); } }
    });
    rejectInPage(page, new page.window.Error('lost'));
    await page.settle(3);
    assert.equal(reports(page).length, 1);
    assert.deepEqual(messages(page).map(message => message.text), [BANNER]);
});

test('oversized fields are clipped to the server limits', async () => {
    const page = await bootReporting();
    rejectInPage(page, { name: 'N'.repeat(150), message: 'M'.repeat(600), stack: 'S'.repeat(9000) });
    await page.settle(2);
    const { body } = reports(page)[0];
    assert.equal(body.name, 'N'.repeat(100));
    assert.equal(body.message, 'M'.repeat(500));
    assert.equal(body.stack, 'S'.repeat(8000));

    page.run('Date.now = () => Number.MAX_SAFE_INTEGER');
    throwInPage(page, "new Error('long source')", `http://localhost/${'x'.repeat(700)}.js`);
    await page.settle(2);
    assert.equal(reports(page)[1].body.source, `http://localhost/${'x'.repeat(700)}.js`.slice(0, 500));
});

// DOM-less unit tests of the helpers, for the paths a page cannot reach.
function reporting(overrides = {}) {
    const posted = [];
    const sandbox = loadScripts(['js/app/error-reporting.js'], {
        navigator: { onLine: true },
        fetch: (url, init) => {
            posted.push({ url, init, body: JSON.parse(init.body) });
            return overrides.fetchResult ? overrides.fetchResult() : Promise.resolve({ ok: true });
        },
        console: { error() {}, log() {}, warn() {} },
        ...overrides.globals
    });
    return { sandbox, posted };
}

test('describeClientError copes with anything that can be thrown', () => {
    const { sandbox } = reporting();
    const describe = code => JSON.parse(sandbox.run(`JSON.stringify(describeClientError(${code}))`));
    assert.deepEqual(describe('null'), { name: '', message: 'null', stack: '' });
    assert.deepEqual(describe('undefined'), { name: '', message: 'undefined', stack: '' });
    assert.deepEqual(describe('42'), { name: '', message: '42', stack: '' });
    assert.deepEqual(describe("'plain text'"), { name: '', message: 'plain text', stack: '' });
    assert.deepEqual(describe('{ status: 500 }'), { name: '', message: '[object Object]', stack: '' });
    assert.deepEqual(describe("{ message: '', toString: () => 'custom reason' }"), { name: '', message: 'custom reason', stack: '' });
    assert.deepEqual(describe("{ name: 7, message: 'bad', stack: 42 }"), { name: '', message: 'bad', stack: '' });
    const fn = describe('function failingCallback() {}');
    assert.equal(fn.name, 'failingCallback');
    assert.equal(fn.message, 'function failingCallback() {}');
});

test('clipClientErrorField stringifies values and treats null as empty', () => {
    const { sandbox } = reporting();
    assert.equal(sandbox.run('clipClientErrorField(null, 5)'), '');
    assert.equal(sandbox.run('clipClientErrorField(undefined, 5)'), '');
    assert.equal(sandbox.run('clipClientErrorField(123456, 3)'), '123');
    assert.equal(sandbox.run("clipClientErrorField('abc', 3)"), 'abc');
});

test('reports are sent without a token or a location when there are none', () => {
    const { sandbox, posted } = reporting({ globals: { location: undefined } });
    sandbox.run("sendClientErrorReport(new RangeError('x'), 'error', undefined)");
    assert.equal(posted.length, 1);
    assert.equal(posted[0].url, '/api/client-errors');
    assert.deepEqual(Object.keys(posted[0].init.headers), ['Content-Type']);
    assert.equal(posted[0].body.url, '');
    assert.equal(posted[0].body.source, '');
    assert.equal(posted[0].body.kind, 'Error');
});

test('a failure while building the report is swallowed', () => {
    const { sandbox, posted } = reporting({
        globals: { localStorage: { getItem() { throw new Error('SecurityError: storage is disabled'); } } }
    });
    assert.doesNotThrow(() => sandbox.run("sendClientErrorReport('boom', 'unhandledrejection')"));
    assert.equal(posted.length, 0);
});

test('a rejected report is swallowed', async () => {
    const { sandbox, posted } = reporting({ fetchResult: () => Promise.reject(new TypeError('Failed to fetch')) });
    sandbox.run("sendClientErrorReport('boom', 'unhandledrejection')");
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(posted[0].body.kind, 'UnhandledRejection');
});

test('pages without the message area only post the report', () => {
    const shown = [];
    const withoutShowMessage = reporting();
    withoutShowMessage.sandbox.run("reportUnexpectedError(new Error('reader failed'), 'error', 'reader.js')");
    assert.equal(withoutShowMessage.posted.length, 1);

    const withoutContainer = reporting({ globals: { showMessage: (...args) => shown.push(args) } });
    withoutContainer.sandbox.run("reportUnexpectedError(new Error('login failed'), 'error', 'login.js')");
    assert.equal(withoutContainer.posted.length, 1);
    assert.deepEqual(shown, []);
});
