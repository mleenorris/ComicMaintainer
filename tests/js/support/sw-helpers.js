'use strict';

// Sandbox for the service worker (wwwroot/sw.js) and the message ports the
// pages use to talk to it.
//
// loadServiceWorker() runs the real script through runWwwrootScript (a file:
// URL filename, so coverage is attributed to sw.js) in a node:vm context that
// stands in for the ServiceWorkerGlobalScope:
//  - `self` is the global itself; addEventListener() records the install,
//    activate, fetch and message listeners, which install()/activate()/
//    fetchEvent()/respond()/message() dispatch as plain event objects and whose
//    waitUntil()/respondWith() promises they await;
//  - `caches` is an in-memory CacheStorage. Entries are keyed by absolute URL
//    and kept in insertion order (a replaced entry moves to the end), as the
//    real Cache.keys() reports them; match() hands out a fresh clone each time
//    and, like the real API, never matches a non-GET request;
//  - `fetch` is "the network": a recording stub programmed per test with
//    routes (see createNetwork), or cut off with `network.offline = true`.
//    Same-origin answers are 'basic' responses, cross-origin ones 'cors';
//  - clients.claim() and skipWaiting() are counted, console output is captured
//    in `logs`, and createClient()/createPort() record what the worker posts.
// worker.restart() evaluates the script again over the same caches and
// network, as a browser does when it wakes up a terminated worker.

const vm = require('node:vm');
const { runWwwrootScript } = require('./sandbox');

const ORIGIN = 'http://localhost';
const APP_VERSION = '2.0.351.0';
const VERSIONED_CACHE = 'comic-maintainer-2-0-351-0';
const DEFAULT_CACHE = 'comic-maintainer-v2';
const IMAGE_CACHE = 'comic-maintainer-images-v1';
const OFFLINE_CACHE = 'comic-maintainer-offline-comics-v1';
const REPLY = Symbol('reply');
const NULL_BODY_STATUSES = new Set([101, 204, 205, 304]);

/** Absolute URL of `input` (a path, URL or Request) on the test origin. */
function absolute(input) {
    const value = input && typeof input === 'object' && 'url' in input ? input.url : String(input);
    return new URL(value, `${ORIGIN}/`).href;
}

/**
 * A network answer with an explicit status, body (JSON-encoded unless a
 * string) and headers; `type` overrides the response type ('basic' for
 * same-origin, 'cors' for cross-origin by default).
 */
function reply(status, body, { headers = {}, type } = {}) {
    return { [REPLY]: true, status, body, headers, type };
}

/** Shorthands for typical static answers. */
const html = body => reply(200, body, { headers: { 'Content-Type': 'text/html; charset=utf-8' } });
const image = body => reply(200, body, { headers: { 'Content-Type': 'image/jpeg' } });
const script = body => reply(200, body, { headers: { 'Content-Type': 'application/javascript' } });

function toResponse(result, url) {
    let response;
    let type;
    if (result instanceof Response) {
        response = result;
    } else if (result === undefined) {
        response = new Response(null, { status: 204 });
    } else if (typeof result === 'number') {
        response = new Response(null, { status: result });
    } else {
        const spec = result && result[REPLY] ? result : { status: 200, body: result, headers: {} };
        type = spec.type;
        const headers = new Headers(spec.headers);
        const nullBody = spec.body === undefined || NULL_BODY_STATUSES.has(spec.status);
        const isText = typeof spec.body === 'string';
        if (!nullBody && !headers.has('Content-Type')) {
            headers.set('Content-Type', isText ? 'text/plain' : 'application/json');
        }
        response = new Response(nullBody ? null : isText ? spec.body : JSON.stringify(spec.body), { status: spec.status, headers });
    }
    if (!Object.prototype.hasOwnProperty.call(response, 'type')) {
        const sameOrigin = new URL(url).origin === ORIGIN;
        Object.defineProperty(response, 'type', { value: type || (sameOrigin ? 'basic' : 'cors') });
    }
    return response;
}

function routeMatches(matcher, call) {
    if (typeof matcher === 'function') return matcher(call);
    if (matcher instanceof RegExp) {
        matcher.lastIndex = 0;
        return matcher.test(`${call.method} ${call.url}`);
    }
    const [method, target] = matcher.includes(' ') ? matcher.split(' ', 2) : ['*', matcher];
    if (method !== '*' && method.toUpperCase() !== call.method) return false;
    if (/^https?:/.test(target)) return target === call.url || target === call.url.split('?')[0];
    return call.sameOrigin && (target === call.path || target === call.path + call.search);
}

/**
 * Recording fetch stub ("the network"). Routes are matched newest-first: a
 * "GET /path" or "/path" (any method) string matches a same-origin path with
 * or without its query (an absolute URL matches cross-origin requests), a
 * RegExp is tested against "METHOD <absolute url>" and a function receives the
 * call. A handler is the answer itself or a (possibly async) function of the
 * call returning one: a Response, a status number, reply(...), undefined (204)
 * or any other value (200 JSON); throwing simulates a network error. Unrouted
 * requests answer 404 and are listed in `unmatched`. While `offline` is set
 * every request fails like a dropped connection.
 */
function createNetwork(routes = []) {
    const table = [];
    const network = {
        calls: [],
        unmatched: [],
        offline: false,
        route(matcher, handler) {
            table.unshift({ matcher, handler });
            return network;
        },
        async fetch(input, init = {}) {
            const isRequest = Boolean(input) && typeof input === 'object' && 'url' in input;
            const url = new URL(String(isRequest ? input.url : input), `${ORIGIN}/`);
            const call = {
                url: url.href,
                path: url.pathname,
                search: url.search,
                query: Object.fromEntries(url.searchParams),
                sameOrigin: url.origin === ORIGIN,
                method: String(init.method || (isRequest ? input.method : 'GET')).toUpperCase(),
                headers: Object.fromEntries(new Headers(init.headers || (isRequest ? input.headers : undefined))),
                init,
                request: isRequest ? input : null
            };
            network.calls.push(call);
            if (network.offline) throw new TypeError('Failed to fetch');
            const entry = table.find(candidate => routeMatches(candidate.matcher, call));
            if (!entry) {
                network.unmatched.push(call);
                return toResponse(reply(404, { error: `No test route for ${call.method} ${call.url}` }), call.url);
            }
            const result = await (typeof entry.handler === 'function' ? entry.handler(call) : entry.handler);
            return toResponse(result, call.url);
        },
        /** Calls whose path (case-insensitive) is `path`. */
        callsTo(path) {
            return network.calls.filter(call => call.path.toLowerCase() === path.toLowerCase());
        }
    };
    // `routes`: { "METHOD /path": handler } or [[matcher, handler], ...] (for RegExp/function matchers).
    for (const [matcher, handler] of Array.isArray(routes) ? routes : Object.entries(routes)) network.route(matcher, handler);
    return network;
}

class FakeCache {
    constructor(storage) {
        this.storage = storage;
        this.entries = new Map();
    }

    async match(request, options = {}) {
        const method = request && typeof request === 'object' && request.method;
        if (method && method !== 'GET' && !options.ignoreMethod) return undefined;
        const entry = this.entries.get(absolute(request));
        return entry ? entry.response.clone() : undefined;
    }

    async put(request, response) {
        const url = absolute(request);
        // Like the real batch "put": the old entry goes and the new one is appended.
        this.entries.delete(url);
        this.entries.set(url, { request: typeof request === 'object' ? request : new Request(url), response });
    }

    async keys() {
        return [...this.entries.values()].map(entry => entry.request);
    }

    async delete(request) {
        return this.entries.delete(absolute(request));
    }

    async add(request) {
        return this.addAll([request]);
    }

    // All or nothing, like the real addAll(): any failed or non-OK response rejects.
    async addAll(requests) {
        const responses = await Promise.all(requests.map(request => this.storage.network.fetch(request)));
        const failed = responses.findIndex(response => !response.ok);
        if (failed >= 0) {
            throw new TypeError(`Cache.addAll(): ${absolute(requests[failed])} answered ${responses[failed].status}`);
        }
        for (const [index, request] of requests.entries()) await this.put(request, responses[index]);
    }

    /** Test hook: the cached URLs, oldest first. */
    urls() {
        return [...this.entries.keys()];
    }
}

class FakeCacheStorage {
    constructor(network) {
        this.network = network;
        this.store = new Map();
    }

    async open(name) {
        if (!this.store.has(name)) this.store.set(name, new FakeCache(this));
        return this.store.get(name);
    }

    async has(name) {
        return this.store.has(name);
    }

    async delete(name) {
        return this.store.delete(name);
    }

    async keys() {
        return [...this.store.keys()];
    }

    async match(request, options) {
        for (const cache of this.store.values()) {
            const found = await cache.match(request, options);
            if (found) return found;
        }
        return undefined;
    }

    /** Test hook: cache names, in creation order. */
    names() {
        return [...this.store.keys()];
    }

    /** Test hook: the URLs cached under `name` (oldest first; [] when there is no such cache). */
    urls(name) {
        const cache = this.store.get(name);
        return cache ? cache.urls() : [];
    }

    /** Test hook: the body of the entry cached under `name` for `url`, or undefined. */
    async text(name, url) {
        const cache = this.store.get(name);
        const response = cache && await cache.match(url);
        return response ? response.text() : undefined;
    }

    /** Test hook: store `{ url: answer }` entries (answers as for network routes). */
    async seed(name, entries) {
        const cache = await this.open(name);
        for (const [url, answer] of Object.entries(entries)) await cache.put(url, toResponse(answer, absolute(url)));
        return cache;
    }
}

/**
 * A Request for `input` on the test origin. `mode` and `destination`, which
 * the Request constructor does not accept for navigations, are applied
 * afterwards the way the browser sets them on the requests it dispatches.
 */
function makeRequest(input, { mode, destination, ...init } = {}) {
    const request = new Request(absolute(input), init);
    if (mode !== undefined) Object.defineProperty(request, 'mode', { value: mode });
    if (destination !== undefined) Object.defineProperty(request, 'destination', { value: destination });
    return request;
}

const NAVIGATION_ACCEPT = 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8';

/** A top-level navigation as the browser dispatches it. */
function navigationRequest(input, { headers = {}, ...init } = {}) {
    return makeRequest(input, {
        mode: 'navigate',
        destination: 'document',
        headers: { Accept: NAVIGATION_ACCEPT, ...headers },
        ...init
    });
}

/** A window client that records the messages the worker posts to it. */
function createClient(id = 'window-1', url = `${ORIGIN}/reader.html`) {
    const client = {
        id,
        url,
        type: 'window',
        messages: [],
        postMessage(message) {
            client.messages.push(structuredClone(message));
        }
    };
    return client;
}

/** A transferred MessagePort that records the worker's replies. */
function createPort() {
    const port = {
        messages: [],
        postMessage(message) {
            port.messages.push(structuredClone(message));
        }
    };
    return port;
}

// A MessageChannel for jsdom pages (jsdom has none): a message posted on one
// port is structured-cloned and delivered to the other port's onmessage in a
// microtask, so no real (event-loop-holding) ports are involved.
class FakeMessagePort {
    constructor() {
        this.onmessage = null;
        this.peer = null;
        this.closed = false;
    }

    postMessage(data) {
        const peer = this.peer;
        const copy = structuredClone(data);
        queueMicrotask(() => {
            if (!peer.closed && typeof peer.onmessage === 'function') peer.onmessage({ data: copy, target: peer });
        });
    }

    start() {}

    close() {
        this.closed = true;
    }
}

class FakeMessageChannel {
    constructor() {
        this.port1 = new FakeMessagePort();
        this.port2 = new FakeMessagePort();
        this.port1.peer = this.port2;
        this.port2.peer = this.port1;
    }
}

/** Network routes for one comic's info, pages and cover, as the reader API serves them. */
function routeComic(network, filePath, { pages = 3, fileName = filePath.split(/[\\/]/).pop() } = {}) {
    const isFor = path => call => call.path === path && call.query.filePath === filePath;
    network.route(isFor('/api/ComicReader/info'), { filePath, fileName, pageCount: pages });
    network.route(isFor('/api/ComicReader/page'), call => image(`${fileName} page ${call.query.page}`));
    network.route(isFor('/api/ComicReader/cover'), image(`${fileName} cover`));
    return network;
}

/** The offline-cache key sw.js stores a downloaded comic's `suffix` ('info', 'cover', 'page/N') under. */
function offlineKey(filePath, suffix) {
    return `${ORIGIN}/__offline-comic/${encodeURIComponent(filePath)}/${suffix}`;
}

/** Let fire-and-forget promise chains (background cache writes) finish. */
async function idle(turns = 10) {
    for (let i = 0; i < turns; i++) await new Promise(resolve => setImmediate(resolve));
}

// waitUntil() may be called again while earlier lifetime promises run (a
// navigation caches its document only once the network answered), so drain
// until no new ones appear.
async function extend(lifetime) {
    for (let done = 0; done < lifetime.length;) {
        const batch = lifetime.slice(done);
        done = lifetime.length;
        await Promise.all(batch);
    }
}

function loadServiceWorker({ routes, network = createNetwork(routes), caches, clients = [] } = {}) {
    const cacheStorage = caches || new FakeCacheStorage(network);
    const listeners = new Map();
    const worker = {
        network,
        caches: cacheStorage,
        clients,
        logs: [],
        claims: 0,
        skipWaitingCalls: 0
    };
    const record = level => (...args) => {
        worker.logs.push({ level, args, text: args.map(arg => (arg instanceof Error ? arg.message : String(arg))).join(' ') });
    };

    const context = vm.createContext({
        console: { log: record('log'), info: record('info'), warn: record('warn'), error: record('error'), debug: record('debug') },
        fetch: (input, init) => network.fetch(input, init),
        caches: cacheStorage,
        clients: {
            claim: async () => {
                worker.claims++;
            },
            matchAll: async () => [...clients],
            get: async id => clients.find(client => client.id === id)
        },
        registration: { scope: `${ORIGIN}/`, installing: null, waiting: null, active: null },
        location: new URL(`${ORIGIN}/sw.js`),
        skipWaiting: async () => {
            worker.skipWaitingCalls++;
        },
        addEventListener: (type, listener) => {
            if (!listeners.has(type)) listeners.set(type, []);
            listeners.get(type).push(listener);
        },
        removeEventListener: (type, listener) => {
            listeners.set(type, (listeners.get(type) || []).filter(candidate => candidate !== listener));
        },
        Request,
        Response,
        Headers,
        URL,
        URLSearchParams,
        MessageChannel,
        AbortController,
        Blob,
        TextEncoder,
        TextDecoder,
        structuredClone,
        queueMicrotask,
        setTimeout,
        clearTimeout
    });
    context.self = context;
    runWwwrootScript('sw.js', context);

    function dispatch(type, init = {}) {
        const lifetime = [];
        let responded = null;
        const event = Object.assign({ type }, init, {
            waitUntil(promise) {
                lifetime.push(Promise.resolve(promise));
            },
            respondWith(response) {
                if (responded) throw new Error('InvalidStateError: respondWith() was already called');
                responded = Promise.resolve(response);
            }
        });
        for (const listener of listeners.get(type) || []) listener.call(context, event);
        return { event, lifetime, responded: () => responded };
    }

    Object.assign(worker, {
        context,
        listeners,
        /** Evaluate code in the worker's global scope (sees its top-level let/const). */
        run: code => vm.runInContext(code, context),
        dispatch,
        /** Fire `install`; rejects when the worker would fail to install. */
        async install() {
            await extend(dispatch('install').lifetime);
        },
        /** Fire `activate` and wait for its cleanup. */
        async activate() {
            await extend(dispatch('activate').lifetime);
        },
        /**
         * Fire `fetch` for a Request (or a URL plus makeRequest() options).
         * Resolves `{ handled, response, event }`: `handled` is false when the
         * worker left the request to the browser (no respondWith()).
         */
        async fetchEvent(input, init) {
            const request = input instanceof Request ? input : makeRequest(input, init);
            const { event, lifetime, responded } = dispatch('fetch', { request, clientId: 'window-1', resultingClientId: '' });
            const pending = responded();
            if (!pending) {
                await extend(lifetime);
                return { handled: false, response: undefined, event };
            }
            const response = await pending;
            await extend(lifetime);
            return { handled: true, response, event };
        },
        /** fetchEvent() for a request the worker must answer; resolves its Response. */
        async respond(input, init) {
            const { handled, response, event } = await worker.fetchEvent(input, init);
            if (!handled) throw new Error(`sw.js left ${event.request.method} ${event.request.url} to the browser`);
            return response;
        },
        /** Fire `message` with `data` (ports/source as a page would send them) and wait for its work. */
        async message(data, { ports = [], source = null } = {}) {
            await extend(dispatch('message', { data, ports, source, origin: ORIGIN, lastEventId: '' }).lifetime);
        },
        /** Messages logged through console.* whose text contains `fragment`. */
        logged(fragment) {
            return worker.logs.filter(entry => entry.text.includes(fragment));
        },
        idle,
        /** A fresh evaluation of sw.js over the same caches and network (a woken-up worker). */
        restart() {
            return loadServiceWorker({ network, caches: cacheStorage, clients });
        }
    });
    return worker;
}

const OFFLINE_PAGE = '<!DOCTYPE html><title>Offline</title><h1>You\'re offline</h1>';

/**
 * Route everything install() fetches on `network`: /api/version (answering
 * `version`), the essential files and every /js/ script. Routes added later
 * take priority, so a test can still make any of them fail.
 */
function installRoutes(network, version = { version: APP_VERSION, platform: '.NET' }) {
    network.route('GET /api/version', version);
    network.route('GET /manifest.json', { name: 'Comic Maintainer', start_url: '/' });
    network.route('GET /offline.html', html(OFFLINE_PAGE));
    network.route(call => call.sameOrigin && call.path.startsWith('/icons/'), reply(200, 'png', { headers: { 'Content-Type': 'image/png' } }));
    network.route(call => call.sameOrigin && call.path.startsWith('/js/'), call => script(`// ${call.path}${call.search}`));
    return network;
}

/** A worker that installed (as version APP_VERSION) and activated; its network log is cleared. */
async function installedWorker(options) {
    const worker = loadServiceWorker(options);
    installRoutes(worker.network);
    await worker.install();
    await worker.activate();
    worker.network.calls.length = 0;
    worker.logs.length = 0;
    return worker;
}

module.exports = {
    ORIGIN,
    APP_VERSION,
    VERSIONED_CACHE,
    DEFAULT_CACHE,
    IMAGE_CACHE,
    OFFLINE_CACHE,
    NAVIGATION_ACCEPT,
    absolute,
    reply,
    html,
    image,
    script,
    toResponse,
    createNetwork,
    FakeCacheStorage,
    makeRequest,
    navigationRequest,
    createClient,
    createPort,
    FakeMessageChannel,
    routeComic,
    offlineKey,
    idle,
    loadServiceWorker,
    OFFLINE_PAGE,
    installRoutes,
    installedWorker
};
