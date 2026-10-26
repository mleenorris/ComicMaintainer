'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const { WWWROOT, createLocalStorage } = require('./support/sandbox');

const readerHtml = fs.readFileSync(path.join(WWWROOT, 'reader.html'), 'utf8');
const start = readerHtml.indexOf("        const PENDING_PROGRESS_KEY = 'pendingReadingProgress';");
const end = readerHtml.indexOf('        // Save reading progress', start);
const progressQueue = readerHtml.slice(start, end);

function loadQueue(initialStorage = {}) {
    const localStorage = createLocalStorage(initialStorage);
    const context = vm.createContext({ Date, Map, Object, Array, JSON, localStorage, console });
    vm.runInContext(progressQueue, context);
    return {
        localStorage,
        run: code => vm.runInContext(code, context)
    };
}

test('reading progress safely queues and clears special property names', () => {
    const { run, localStorage } = loadQueue();

    run("queuePendingProgress('__proto__', 4); queuePendingProgress('constructor', 7)");

    assert.equal(run("getPendingProgress('__proto__')"), 4);
    assert.equal(run("getPendingProgress('constructor')"), 7);
    assert.deepEqual(JSON.parse(localStorage.getItem('pendingReadingProgress')).map(([filePath]) => filePath),
        ['__proto__', 'constructor']);

    run("clearPendingProgress('__proto__')");
    assert.equal(run("getPendingProgress('__proto__')"), null);
    assert.equal(run("getPendingProgress('constructor')"), 7);
});

test('reading progress reads entries persisted by the previous object format', () => {
    const { run } = loadQueue({
        pendingReadingProgress: '{"__proto__":{"page":9,"updatedAt":1},"/comics/a.cbz":{"page":3,"updatedAt":2}}'
    });

    assert.equal(run("getPendingProgress('__proto__')"), 9);
    assert.equal(run("getPendingProgress('/comics/a.cbz')"), 3);
});
