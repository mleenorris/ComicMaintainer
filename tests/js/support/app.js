'use strict';

// Shared setup for tests that boot the signed-in library SPA (index.html), so
// each area's tests only route the requests they are about.
//
// initializeApp() (js/main.js) checks the session, then requests
// /api/auth/user, /api/preferences, /api/active-job, /api/files/counts,
// /api/files/series, /api/watcher/status and the current route's data (e.g. /api/overview for #/home, /api/settings for #/settings).
// Unrouted requests answer 404, which the boot tolerates; bootRoutes() only
// answers the session checks.

const fs = require('node:fs');
const path = require('node:path');
const { loadPage } = require('./dom');
const { WWWROOT } = require('./sandbox');

function jwt(payload) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature`;
}

// A structurally valid, unexpired token: the client only decodes the payload
// to check `exp`, it never verifies the signature.
const validToken = (claims = {}) => jwt({ sub: 'alice', exp: Math.floor(Date.now() / 1000) + 3600, ...claims });

// GET /api/auth/user as AuthController.GetCurrentUser returns it. A read-only
// user is `{ canModifyLibrary: false, canAdminister: false }`.
function currentUser(overrides = {}) {
    return {
        username: 'alice',
        userId: 'user-1',
        email: 'alice@example.com',
        roles: ['Admin'],
        authMethod: 'jwt',
        canModifyLibrary: true,
        canAdminister: true,
        ...overrides
    };
}

function bootRoutes(user = {}) {
    return {
        'GET /api/auth/status': { autheliaEnabled: false, isAuthenticated: false },
        'GET /api/auth/user': currentUser(user)
    };
}

// Loads index.html at `hash` with a signed-in session and waits for the boot to
// settle. `routes` override the defaults (later entries win); `user` overrides
// fields of the /api/auth/user response; other options go to loadPage().
async function bootApp({ hash = '#/home', routes = {}, user = {}, localStorage = {}, ...options } = {}) {
    const page = loadPage({
        url: `http://localhost/index.html${hash}`,
        ...options,
        localStorage: { jwt_token: validToken(), username: 'alice', ...localStorage },
        routes: { ...bootRoutes(user), ...routes }
    });
    await page.settle();
    return page;
}

// A promise with its settle functions exposed, for holding a route's response
// open while a test asserts a loading state.
function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((res, rej) => {
        resolve = res;
        reject = rej;
    });
    return { promise, resolve, reject };
}

// A loadPage() `beforeScripts` hook that adds css/main.css's read-only gate
// (body.user-read-only hides every [data-requires-write] control) to the page,
// for tests of what a ReadOnly user is shown: the harness loads no stylesheets.
function addReadOnlyGate(window) {
    const css = fs.readFileSync(path.join(WWWROOT, 'css', 'main.css'), 'utf8');
    const rule = /body\.user-read-only \[data-requires-write\]\s*\{[^}]*\}/.exec(css);
    if (!rule) throw new Error('css/main.css has no read-only gate rule');
    const style = window.document.createElement('style');
    style.textContent = rule[0];
    window.document.head.appendChild(style);
}

// Whether `element` is displayed. jsdom's computed display doesn't account for
// an ancestor's display: none, so the whole chain is checked.
function isDisplayed(element) {
    const view = element.ownerDocument.defaultView;
    for (let node = element; node; node = node.parentElement) {
        if (view.getComputedStyle(node).display === 'none') return false;
    }
    return true;
}

module.exports = { jwt, validToken, currentUser, bootRoutes, bootApp, deferred, addReadOnlyGate, isDisplayed };
