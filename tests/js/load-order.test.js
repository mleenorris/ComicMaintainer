'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { WWWROOT, indexHtmlScripts, loadScripts } = require('./support/sandbox');

test('index.html loads shared.js first and main.js last', () => {
    const scripts = indexHtmlScripts();
    assert.equal(scripts[0], 'js/shared.js');
    assert.equal(scripts.at(-1), 'js/main.js');
    assert.equal(new Set(scripts).size, scripts.length);
});

test('every script under wwwroot/js is loaded by index.html', () => {
    const jsRoot = path.join(WWWROOT, 'js');
    const onDisk = fs.readdirSync(jsRoot, { recursive: true })
        .filter(file => file.endsWith('.js'))
        .map(file => 'js/' + file.split(path.sep).join('/'))
        .sort();
    assert.deepEqual(onDisk, [...indexHtmlScripts()].sort());
});

test('the app scripts load in order without touching later scripts', () => {
    // Each script runs only after the previous ones, exactly as in the
    // browser; a load-time call into a later script throws a ReferenceError.
    const { listeners, run } = loadScripts(indexHtmlScripts());

    // While the document is still loading, main.js defers initialisation to
    // DOMContentLoaded, and initializeApp is registered first.
    const domReady = listeners.filter(l => l.target === 'document' && l.type === 'DOMContentLoaded');
    assert.ok(domReady.length >= 1);
    assert.equal(domReady[0].handler, run('initializeApp'));

    // The last-resort error reporters are registered by main.js.
    const windowEvents = listeners.filter(l => l.target === 'window').map(l => l.type);
    assert.ok(windowEvents.includes('error'));
    assert.ok(windowEvents.includes('unhandledrejection'));
    assert.equal(windowEvents.filter(type => type === 'resize').length, 1);
});
