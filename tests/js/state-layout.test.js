'use strict';

// js/app/state.js — the saved series layout is read once at load. Only
// 'list', 'grid' and 'compact' are honoured. Anything else, or storage that
// can't be read, means automatic (null).

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadScripts, createLocalStorage } = require('./support/sandbox');

const layoutAfterLoading = localStorage => loadScripts(['js/app/state.js'], { localStorage }).run('seriesLayoutPreference');

test('a saved list, grid or compact layout is restored', () => {
    for (const layout of ['list', 'grid', 'compact']) {
        assert.equal(layoutAfterLoading(createLocalStorage({ seriesLayout: layout })), layout);
    }
});

test('no saved layout, or an unknown one, means automatic', () => {
    for (const stored of [{}, { seriesLayout: '' }, { seriesLayout: 'masonry' }, { seriesLayout: 'Grid' }]) {
        assert.equal(layoutAfterLoading(createLocalStorage(stored)), null, JSON.stringify(stored));
    }
});

test('storage that cannot be read (for example, blocked by privacy settings) also means automatic', () => {
    const blocked = { getItem() { throw new Error('The operation is insecure.'); } };
    assert.equal(layoutAfterLoading(blocked), null);
});
