'use strict';

// jsdom page harness: loads a real wwwroot HTML page into jsdom and runs its
// scripts through node:vm in the page's own context, so tests exercise the
// shipped markup and scripts together. Scripts run with file: URL filenames
// (see runWwwrootScript), which is what attributes them in coverage reports.
//
// Browser APIs jsdom lacks (fetch, matchMedia, IntersectionObserver,
// EventSource, service workers, clipboard, scrolling...) are replaced by
// recording stubs; every page is closed automatically when its test file ends.

const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { after } = require('node:test');
const { JSDOM, VirtualConsole } = require('jsdom');
const { WWWROOT, runWwwrootScript } = require('./sandbox');

// Captured before any test can enable node:test's mock timers: jsdom schedules
// page timers through Node's global setTimeout at call time, so
// `t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] })` +
// `t.mock.timers.tick(ms)` drives the page's timers, while settle()/waitFor()
// keep yielding real event-loop turns. jsdom re-arms setInterval as chained
// timeouts, so tick one interval period at a time.
const realSetTimeout = globalThis.setTimeout;
const nextTurn = (ms = 0) => new Promise(resolve => realSetTimeout(resolve, ms));

const TEST_APP_VERSION = 'test-version';
const CONSOLE_METHODS = ['log', 'info', 'warn', 'error', 'debug', 'trace'];
const NULL_BODY_STATUSES = new Set([101, 204, 205, 304]);
const RESPONSE_SPEC = Symbol('responseSpec');

const openPages = new Set();
after(() => {
    for (const page of [...openPages]) page.close();
});

// jsdom reports a cross-document navigation only as "not implemented", without
// its target. Hooking its Location implementation (an internal module, hence
// the guard) records every navigation in page.navigations so tests can assert
// redirects such as `location.href = '/login.html'`.
const pagesByDocument = new WeakMap();
(function recordNavigations() {
    try {
        const jsdomDir = path.dirname(require.resolve('jsdom/package.json'));
        const { implementation: LocationImpl } = require(path.join(jsdomDir, 'lib', 'jsdom', 'living', 'window', 'Location-impl.js'));
        const { serializeURL } = require(require.resolve('whatwg-url', { paths: [jsdomDir] }));
        const { _locationObjectNavigate: navigate, reload } = LocationImpl.prototype;
        LocationImpl.prototype._locationObjectNavigate = function (url, flags = {}) {
            pagesByDocument.get(this._relevantDocument)?.navigations.push({ url: serializeURL(url), replace: Boolean(flags.replacement) });
            return navigate.call(this, url, flags);
        };
        LocationImpl.prototype.reload = function () {
            pagesByDocument.get(this._relevantDocument)?.navigations.push({ url: serializeURL(this._url), reload: true });
            return reload.call(this);
        };
    } catch {
        // Another jsdom layout: page.navigations simply stays empty.
    }
})();

/**
 * Copy a value out of the page's realm into plain test-realm data, so
 * assert.deepStrictEqual (which compares prototypes) accepts page arrays/objects.
 */
function plain(value) {
    return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

/** A fetch result with an explicit status, body (JSON-encoded unless a string) and headers. */
function respond(status, body, headers = {}) {
    return { [RESPONSE_SPEC]: true, status, body, headers };
}

function toResponse(result) {
    if (result instanceof Response) return result;
    if (result === undefined) return new Response(null, { status: 204 });
    if (typeof result === 'number') return new Response(null, { status: result });
    const spec = result && result[RESPONSE_SPEC] ? result : { status: 200, body: result, headers: {} };
    if (NULL_BODY_STATUSES.has(spec.status) || spec.body === undefined) {
        return new Response(null, { status: spec.status, headers: spec.headers });
    }
    const isText = typeof spec.body === 'string';
    return new Response(isText ? spec.body : JSON.stringify(spec.body), {
        status: spec.status,
        headers: { 'Content-Type': isText ? 'text/plain' : 'application/json', ...spec.headers }
    });
}

function normalizeHeaders(headers) {
    if (!headers) return {};
    const entries = typeof headers.entries === 'function' ? [...headers.entries()] : Object.entries(headers);
    return Object.fromEntries(entries.map(([key, value]) => [key.toLowerCase(), String(value)]));
}

function parseBody(body) {
    if (typeof body !== 'string') return body;
    try {
        return JSON.parse(body);
    } catch {
        return body;
    }
}

function routeMatches(matcher, call) {
    if (typeof matcher === 'function') return matcher(call);
    if (matcher instanceof RegExp) {
        matcher.lastIndex = 0;
        return matcher.test(`${call.method} ${call.url}`);
    }
    const [method, target] = matcher.includes(' ') ? matcher.split(' ', 2) : ['*', matcher];
    return (method === '*' || method.toUpperCase() === call.method) && (target === call.url || target === call.path);
}

function abortError() {
    const error = new Error('The operation was aborted.');
    error.name = 'AbortError';
    return error;
}

/**
 * Recording fetch stub. Routes are matched newest-first against
 * "METHOD /path?query": a "GET /api/x" or "/api/x" (any method) string matches
 * the path with or without its query, a RegExp is tested against
 * "METHOD /path?query", and a function receives the call. A handler is either
 * the result itself or a (possibly async) function of the call returning one:
 * a Response, a status number, respond(status, body, headers), undefined (204)
 * or any other value (200 JSON). Unrouted requests get a 404 and are listed in
 * `unmatched`.
 */
function createFetchStub(window, routes) {
    const table = [];
    const calls = [];
    const unmatched = [];
    const route = (matcher, handler) => {
        table.unshift({ matcher, handler });
    };
    for (const [matcher, handler] of Object.entries(routes || {})) route(matcher, handler);

    async function fetch(input, init = {}) {
        const isRequest = input && typeof input === 'object' && 'url' in input && !(input instanceof window.URL);
        const url = new window.URL(String(isRequest ? input.url : input), window.location.href);
        const call = {
            url: url.pathname + url.search,
            path: url.pathname,
            query: Object.fromEntries(url.searchParams),
            method: String(init.method || (isRequest && input.method) || 'GET').toUpperCase(),
            headers: normalizeHeaders(init.headers || (isRequest ? input.headers : null)),
            body: parseBody(init.body),
            init
        };
        calls.push(call);
        const signal = init.signal;
        if (signal && signal.aborted) throw abortError();

        const entry = table.find(candidate => routeMatches(candidate.matcher, call));
        if (!entry) {
            unmatched.push(call);
            return toResponse(respond(404, { error: `No test route for ${call.method} ${call.url}` }));
        }
        const pending = Promise.resolve(typeof entry.handler === 'function' ? entry.handler(call) : entry.handler);
        if (!signal) return toResponse(await pending);
        return new Promise((resolve, reject) => {
            signal.addEventListener('abort', () => reject(abortError()), { once: true });
            pending.then(result => resolve(toResponse(result)), reject);
        });
    }

    return { fetch, calls, unmatched, route };
}

function createMediaQueryList(window, query, matches) {
    const listeners = new Set();
    return {
        media: query,
        matches,
        onchange: null,
        addEventListener: (_type, listener) => listeners.add(listener),
        removeEventListener: (_type, listener) => listeners.delete(listener),
        addListener: listener => listeners.add(listener),
        removeListener: listener => listeners.delete(listener),
        dispatchEvent: () => true,
        /** Test hook: flip `matches` and notify listeners like a viewport change would. */
        change(nextMatches) {
            this.matches = nextMatches;
            const event = { matches: nextMatches, media: query };
            for (const listener of [...listeners]) listener.call(this, event);
            if (typeof this.onchange === 'function') this.onchange(event);
        }
    };
}

function installBrowserStubs(window, page, options) {
    window.fetch = page.fetchStub.fetch;
    window.Response = Response;
    window.Request = Request;
    window.Headers = Headers;
    if (typeof window.structuredClone !== 'function') window.structuredClone = structuredClone;

    const media = options.media || {};
    window.matchMedia = query => {
        const list = createMediaQueryList(window, query, Boolean(media[query]));
        page.mediaQueries.push(list);
        return list;
    };

    window.scrollTo = (x, y) => {
        const top = typeof x === 'object' && x !== null ? x.top : y;
        page.scrolls.push({ x, y });
        if (typeof top === 'number') {
            Object.defineProperty(window, 'scrollY', { value: top, configurable: true, writable: true });
        }
    };
    window.scroll = window.scrollTo;
    window.scrollBy = () => {};
    window.Element.prototype.scrollIntoView = function scrollIntoView() {
        page.scrolledIntoView.push(this);
    };
    window.Element.prototype.scrollTo = function scrollTo() {};

    window.IntersectionObserver = class IntersectionObserver {
        constructor(callback, init) {
            this.callback = callback;
            this.options = init;
            this.targets = new Set();
            page.intersectionObservers.push(this);
        }
        observe(target) { this.targets.add(target); }
        unobserve(target) { this.targets.delete(target); }
        disconnect() { this.targets.clear(); }
        takeRecords() { return []; }
        /** Test hook: report every observed target (or `targets`) as (not) intersecting. */
        trigger(isIntersecting = true, targets = [...this.targets]) {
            this.callback(targets.map(target => ({ target, isIntersecting, intersectionRatio: isIntersecting ? 1 : 0 })), this);
        }
    };

    window.EventSource = class EventSource {
        constructor(url, init) {
            this.url = url;
            this.withCredentials = Boolean(init && init.withCredentials);
            this.readyState = 0;
            this.listeners = new Map();
            this.onopen = null;
            this.onmessage = null;
            this.onerror = null;
            page.eventSources.push(this);
        }
        addEventListener(type, listener) {
            if (!this.listeners.has(type)) this.listeners.set(type, new Set());
            this.listeners.get(type).add(listener);
        }
        removeEventListener(type, listener) {
            this.listeners.get(type)?.delete(listener);
        }
        close() { this.readyState = 2; }
        /** Test hook: deliver an event (`data` objects are JSON-encoded like the server does). */
        emit(type, data) {
            if (type === 'open') this.readyState = 1;
            const event = { type, data: typeof data === 'string' || data === undefined ? data : JSON.stringify(data), target: this };
            const handler = this[`on${type}`];
            if (typeof handler === 'function') handler.call(this, event);
            for (const listener of [...(this.listeners.get(type) || [])]) listener.call(this, event);
        }
    };
    window.EventSource.CONNECTING = 0;
    window.EventSource.OPEN = 1;
    window.EventSource.CLOSED = 2;

    if (options.serviceWorker !== false) {
        const swListeners = new Map();
        const serviceWorker = {
            controller: null,
            ready: Promise.resolve({ active: null, update: async () => {} }),
            register: async (url, init) => {
                page.serviceWorkerRegistrations.push({ url, options: init });
                return { scope: '/', installing: null, waiting: null, active: null, update: async () => {}, addEventListener() {} };
            },
            getRegistration: async () => undefined,
            getRegistrations: async () => [],
            addEventListener: (type, listener) => {
                if (!swListeners.has(type)) swListeners.set(type, new Set());
                swListeners.get(type).add(listener);
            },
            removeEventListener: (type, listener) => swListeners.get(type)?.delete(listener),
            /** Test hook: fire a navigator.serviceWorker event such as 'message' or 'controllerchange'. */
            emit(type, event = {}) {
                for (const listener of [...(swListeners.get(type) || [])]) listener({ type, ...event });
            }
        };
        Object.defineProperty(window.navigator, 'serviceWorker', { value: serviceWorker, configurable: true });
    }

    Object.defineProperty(window.navigator, 'clipboard', {
        value: {
            writeText: async text => { page.clipboard.push(String(text)); },
            readText: async () => page.clipboard.at(-1) ?? ''
        },
        configurable: true
    });

    let objectUrlCount = 0;
    window.URL.createObjectURL = blob => {
        const url = `blob:http://localhost/test-${++objectUrlCount}`;
        page.objectUrls.set(url, blob);
        return url;
    };
    window.URL.revokeObjectURL = url => {
        page.objectUrls.delete(url);
    };

    for (const kind of ['alert', 'confirm', 'prompt']) {
        window[kind] = message => {
            page.dialogs.push({ kind, message });
            if (kind === 'confirm') return options.confirm ?? true;
            if (kind === 'prompt') return options.prompt ?? null;
            return undefined;
        };
    }
}

const SCRIPT_TAG = /<script\b([^>]*)>([\s\S]*?)<\/script>/gi;

/**
 * Disable the page's <script> elements (jsdom would otherwise run them without
 * coverage, or try to fetch them) and list them in document order.
 */
function extractScripts(markup) {
    const scripts = [];
    const disabled = markup.replace(SCRIPT_TAG, (_whole, attrs, body) => {
        const src = /\bsrc\s*=\s*"([^"]+)"/i.exec(attrs);
        const type = /\btype\s*=\s*"([^"]+)"/i.exec(attrs);
        const runnable = !type || /^(text|application)\/(java|ecma)script$/i.test(type[1]);
        if (runnable) {
            scripts.push(src ? { src: src[1].split('?')[0].replace(/^\//, '') } : { code: body });
        }
        return `<script type="text/x-disabled-in-tests"${attrs}>${body}</script>`;
    });
    return { markup: disabled, scripts };
}

/**
 * Load a wwwroot page into jsdom and run its scripts.
 *
 * Options:
 *  - html: wwwroot-relative page (default 'index.html'); `markup` supplies raw HTML instead.
 *  - url: page URL (default http://localhost/<html>).
 *  - scripts: wwwroot-relative scripts to run instead of the page's own
 *    external scripts. The page's inline scripts still run first unless
 *    `inlineScripts: false`. Entries may also be `{ code }` for ad-hoc code.
 *  - routes: initial fetch routes (see createFetchStub); add more with page.route().
 *  - localStorage / sessionStorage: initial entries.
 *  - media: { '<media query>': true } results for matchMedia.
 *  - confirm / prompt: return values for window.confirm / window.prompt.
 *  - serviceWorker: false to leave navigator.serviceWorker undefined.
 *  - globals: properties assigned onto window just before scripts run.
 *  - beforeScripts(window, page): hook run just before scripts run.
 *
 * The page's document is still 'loading' while its scripts run, so
 * DOMContentLoaded handlers fire afterwards, as in a browser; `await page.settle()`
 * to let them (and any fetches they start) finish.
 */
function loadPage(options = {}) {
    const html = options.html || 'index.html';
    const source = options.markup ?? fs.readFileSync(path.join(WWWROOT, ...html.split('/')), 'utf8');
    const { markup, scripts: pageScripts } = extractScripts(source.replaceAll('__APP_VERSION__', TEST_APP_VERSION));

    const virtualConsole = new VirtualConsole();
    const page = {
        consoleMessages: [],
        errors: [],
        dialogs: [],
        clipboard: [],
        scrolls: [],
        scrolledIntoView: [],
        mediaQueries: [],
        intersectionObservers: [],
        eventSources: [],
        serviceWorkerRegistrations: [],
        navigations: [],
        objectUrls: new Map()
    };
    for (const method of CONSOLE_METHODS) {
        virtualConsole.on(method, (...args) => page.consoleMessages.push({ type: method, args }));
    }
    virtualConsole.on('jsdomError', error => page.errors.push(error));

    const dom = new JSDOM(markup, {
        url: options.url || `http://localhost/${options.markup !== undefined ? '' : html}`,
        runScripts: 'dangerously',
        pretendToBeVisual: true,
        virtualConsole
    });
    const { window } = dom;
    const context = dom.getInternalVMContext();
    pagesByDocument.set(window._document, page);

    Object.assign(page, {
        dom,
        window,
        document: window.document,
        context,
        fetchStub: createFetchStub(window, options.routes),
        /** Evaluate code in the page's global scope (sees top-level let/const too). */
        run: code => vm.runInContext(code, context),
        /** run(), copied into plain test-realm data (see plain()). */
        evaluate: code => plain(vm.runInContext(code, context)),
        $: selector => window.document.querySelector(selector),
        $$: selector => [...window.document.querySelectorAll(selector)],
        /** Click an element (or the first match of a selector), running inline onclick handlers. */
        click(target) {
            const element = typeof target === 'string' ? window.document.querySelector(target) : target;
            if (!element) throw new Error(`No element to click: ${target}`);
            element.click();
            return element;
        },
        /** Set a form control's value and fire input + change, as typing/selecting would. */
        input(target, value) {
            const element = typeof target === 'string' ? window.document.querySelector(target) : target;
            if (!element) throw new Error(`No element to fill: ${target}`);
            if (element.type === 'checkbox' || element.type === 'radio') element.checked = Boolean(value);
            else element.value = value;
            element.dispatchEvent(new window.Event('input', { bubbles: true }));
            element.dispatchEvent(new window.Event('change', { bubbles: true }));
            return element;
        },
        /** Let pending timers, promise chains and fetches run (`rounds` macrotask turns). */
        async settle(rounds = 10) {
            for (let i = 0; i < rounds; i++) await nextTurn();
        },
        /** Poll `predicate` until it is truthy; throws after `timeout` ms. */
        async waitFor(predicate, { timeout = 2000, interval = 5 } = {}) {
            const deadline = Date.now() + timeout;
            for (;;) {
                const value = await predicate();
                if (value) return value;
                if (Date.now() > deadline) throw new Error(`waitFor timed out after ${timeout} ms`);
                await nextTurn(interval);
            }
        },
        close() {
            if (!openPages.has(page)) return;
            openPages.delete(page);
            window.close();
        }
    });
    page.route = page.fetchStub.route;
    page.fetchCalls = page.fetchStub.calls;
    page.unmatchedRequests = page.fetchStub.unmatched;
    openPages.add(page);

    installBrowserStubs(window, page, options);
    for (const [key, value] of Object.entries(options.localStorage || {})) window.localStorage.setItem(key, value);
    for (const [key, value] of Object.entries(options.sessionStorage || {})) window.sessionStorage.setItem(key, value);
    Object.assign(window, options.globals || {});
    if (options.beforeScripts) options.beforeScripts(window, page);

    const scripts = options.scripts
        ? [
            ...(options.inlineScripts === false ? [] : pageScripts.filter(script => script.code !== undefined)),
            ...options.scripts.map(script => (typeof script === 'string' ? { src: script } : script))
        ]
        : pageScripts;
    scripts.forEach((script, index) => {
        if (script.src) runWwwrootScript(script.src, context);
        // Classic-script semantics: top-level let/const stay visible to later scripts.
        else vm.runInContext(script.code, context, { filename: `inline-script-${index}-${html}` });
    });

    return page;
}

module.exports = { loadPage, respond, toResponse, plain, TEST_APP_VERSION };
