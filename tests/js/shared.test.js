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

test('apiUrl leaves a path at the domain root as it is, and adds a missing leading slash', () => {
    const { run } = load();
    assert.equal(run("apiUrl('/api/version')"), '/api/version');
    assert.equal(run("apiUrl('api/version')"), '/api/version');
});
