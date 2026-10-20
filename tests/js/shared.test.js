'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadScripts, createLocalStorage } = require('./support/sandbox');

const plain = value => JSON.parse(JSON.stringify(value));

function load(storage = {}) {
    return loadScripts(['js/shared.js'], { localStorage: createLocalStorage(storage) });
}

test('isAutheliaAuthenticated reflects the stored flag', () => {
    assert.equal(load().run('isAutheliaAuthenticated()'), false);
    assert.equal(load({ authelia_authenticated: 'false' }).run('isAutheliaAuthenticated()'), false);
    assert.equal(load({ authelia_authenticated: 'true' }).run('isAutheliaAuthenticated()'), true);
});

test('buildCredentialedFetchOptions always includes credentials', () => {
    const { run } = load();
    assert.deepEqual(plain(run('buildCredentialedFetchOptions()')), { credentials: 'include' });
    assert.deepEqual(plain(run('buildCredentialedFetchOptions(undefined, "")')), { credentials: 'include' });
});

test('buildCredentialedFetchOptions attaches the bearer token for JWT sessions', () => {
    const { run } = load();
    const options = plain(run('buildCredentialedFetchOptions({ method: "POST", headers: { "Content-Type": "application/json" } }, "abc")'));
    assert.deepEqual(options, {
        credentials: 'include',
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: ['Bearer', 'abc'].join(' ') }
    });
});

test('buildCredentialedFetchOptions omits the token for Authelia sessions', () => {
    const { run } = load({ authelia_authenticated: 'true' });
    assert.deepEqual(plain(run('buildCredentialedFetchOptions({ cache: "no-store" }, "abc")')),
        { credentials: 'include', cache: 'no-store' });
});

test('buildCredentialedFetchOptions does not mutate the caller\'s headers', () => {
    const { run } = load();
    assert.deepEqual(plain(run('const h = { A: "1" }; buildCredentialedFetchOptions({ headers: h }, "t"); h')), { A: '1' });
});
