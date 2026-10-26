'use strict';

// Loads the SPA's classic scripts into a node:vm context, the same way the
// browser does: each file is a separate script sharing one global scope, so
// top-level declarations in one are visible to the next.

const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { pathToFileURL } = require('node:url');

const WWWROOT = path.resolve(__dirname, '..', '..', '..', 'src', 'ComicMaintainer.WebApi', 'wwwroot');

/**
 * Run a wwwroot script in a vm context. The filename must be a file: URL:
 * node's test coverage only attributes code to scripts whose URL is one, so a
 * relative name would leave every SPA script out of the coverage report.
 */
function runWwwrootScript(script, context) {
    const file = path.join(WWWROOT, ...script.split('/'));
    vm.runInContext(fs.readFileSync(file, 'utf8'), context, { filename: pathToFileURL(file).href });
}

/** Script paths (relative to wwwroot) index.html loads, in order. */
function indexHtmlScripts() {
    const html = fs.readFileSync(path.join(WWWROOT, 'index.html'), 'utf8');
    return [...html.matchAll(/<script\s+src="\/(js\/[^"?]+\.js)\?v=__APP_VERSION__"\s*><\/script>/g)]
        .map(match => match[1]);
}

function createLocalStorage(initial = {}) {
    const store = new Map(Object.entries(initial));
    return {
        getItem: key => (store.has(key) ? store.get(key) : null),
        setItem: (key, value) => { store.set(key, String(value)); },
        removeItem: key => { store.delete(key); },
        clear: () => store.clear()
    };
}

function noop() {}

/**
 * Minimal browser-like globals. Only what the scripts touch while loading
 * (plus what the pure helpers under test need) is provided; anything else
 * is left undefined so an unexpected load-time dependency fails loudly.
 */
function createBrowserGlobals(overrides = {}) {
    const listeners = [];
    const addEventListener = target => (type, handler) => listeners.push({ target, type, handler });
    const createdElements = [];

    const document = {
        readyState: 'loading',
        addEventListener: addEventListener('document'),
        removeEventListener: noop,
        getElementById: () => null,
        querySelector: () => null,
        querySelectorAll: () => [],
        createElement: tagName => {
            const element = { tagName: String(tagName).toUpperCase(), innerHTML: '' };
            createdElements.push(element);
            return element;
        }
    };

    const globals = {
        console,
        document,
        location: { hash: '', search: '', pathname: '/', href: 'http://localhost/' },
        navigator: {},
        localStorage: createLocalStorage(),
        sessionStorage: createLocalStorage(),
        fetch: async () => { throw new Error('fetch is not available in tests'); },
        setTimeout,
        clearTimeout,
        setInterval,
        clearInterval,
        requestAnimationFrame: callback => setTimeout(callback, 0),
        matchMedia: () => ({ matches: false, addEventListener: noop, removeEventListener: noop }),
        addEventListener: addEventListener('window'),
        removeEventListener: noop,
        URL,
        URLSearchParams,
        TextEncoder,
        btoa,
        atob,
        // index.html defines these in an inline script before the app scripts.
        BASE_PATH: '',
        apiUrl: p => (p.startsWith('/') ? p : '/' + p),
        ...overrides
    };

    return { globals, listeners, createdElements };
}

/**
 * Create a context and run the given wwwroot-relative scripts in order.
 * Returns the context (globals + helpers) plus recorded event listeners.
 */
function loadScripts(scripts, overrides = {}) {
    const { globals, listeners, createdElements } = createBrowserGlobals(overrides);
    const context = vm.createContext(globals);
    // Browser scripts reach the global object as `window` and `self`.
    vm.runInContext('var window = globalThis; var self = globalThis;', context);
    for (const script of scripts) {
        runWwwrootScript(script, context);
    }
    return {
        context,
        listeners,
        createdElements,
        /** Evaluate an expression in the scripts' global scope. */
        run: code => vm.runInContext(code, context)
    };
}

module.exports = { WWWROOT, indexHtmlScripts, loadScripts, createLocalStorage, runWwwrootScript };
