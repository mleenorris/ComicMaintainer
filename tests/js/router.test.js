'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadScripts } = require('./support/sandbox');

const plain = value => JSON.parse(JSON.stringify(value));

function parseHash(hash) {
    const sandbox = loadScripts(['js/shared.js', 'js/app/templating.js', 'js/app/router.js']);
    sandbox.context.location.hash = hash;
    return plain(sandbox.run('parseHash()'));
}

test('parseHash maps the top-level pages', () => {
    assert.deepEqual(parseHash('#/home'), { view: 'home' });
    assert.deepEqual(parseHash('#/library'), { view: 'library' });
    assert.deepEqual(parseHash('#/settings'), { view: 'settings' });
});

test('parseHash extracts and decodes the series id', () => {
    assert.deepEqual(parseHash('#/series/42'), { view: 'series', seriesId: '42' });
    assert.deepEqual(parseHash('#/series/Batman%20%26%20Robin%2F2011'),
        { view: 'series', seriesId: 'Batman & Robin/2011' });
});

test('parseHash falls back to the overview for empty or unknown routes', () => {
    assert.deepEqual(parseHash(''), { view: 'home' });
    assert.deepEqual(parseHash('#'), { view: 'home' });
    assert.deepEqual(parseHash('#/nope'), { view: 'home' });
    assert.deepEqual(parseHash('#/library/extra'), { view: 'home' });
});
