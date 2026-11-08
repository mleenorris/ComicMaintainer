'use strict';

// js/app/selection.js: the size, age and file-name formatting the library's
// views share.

const test = require('node:test');
const assert = require('node:assert/strict');
const { bootLibrary } = require('./support/library-helpers');

const MiB = 1024 * 1024;

test('file sizes read in the largest unit that keeps them above one', async () => {
    const page = await bootLibrary();

    assert.deepEqual(
        [500, 1536, 5 * MiB, 2.5 * 1024 * MiB, 3 * 1024 * 1024 * MiB].map(size => page.run(`formatFileSize(${size})`)),
        ['500 B', '1.5 KB', '5.0 MB', '2.5 GB', '3.0 TB']);
});

/** formatModifiedDate() of each timestamp, as read at the moment `now`. */
function agesAt(page, now, timestamps) {
    page.run(`window.Date = class extends Date {
        constructor(...args) { if (args.length) super(...args); else super(${now}); }
        static now() { return ${now}; }
    }`);
    return timestamps.map(seconds => page.run(`formatModifiedDate(${seconds})`));
}

const seconds = ms => Math.floor(ms / 1000);

test('a file\'s age reads as a relative time for the past week, then as a date', async () => {
    const page = await bootLibrary();
    const now = new Date(2024, 5, 15, 12, 0, 0).getTime();

    assert.deepEqual(agesAt(page, now, [
        seconds(now) - 20,
        seconds(now) - 5 * 60 - 10,
        seconds(now) - 3 * 3600 - 10,
        seconds(now) - 30 * 3600,
        seconds(now) - 3 * 86400 - 10,
        seconds(new Date(2024, 2, 1, 12).getTime()),
        seconds(new Date(2023, 11, 25, 12).getTime())
    ]), ['Just now', '5 min ago', '3h ago', 'Yesterday', '3 days ago', '03-01', '2023-12-25']);
});

test('a file dated ahead of the browser\'s clock reads Just now for up to a minute, then as a date', async () => {
    const page = await bootLibrary();
    const now = new Date(2024, 5, 15, 12, 0, 0).getTime();

    assert.deepEqual(agesAt(page, now, [
        seconds(now) + 1,
        seconds(now) + 59,
        seconds(now) + 60,
        seconds(now) + 2 * 60,
        seconds(now) + 2 * 3600,
        seconds(now) + 3 * 86400,
        seconds(new Date(2025, 0, 2, 12).getTime())
    ]), ['Just now', 'Just now', 'Just now', '06-15', '06-15', '06-18', '2025-01-02']);
});

test('a path\'s display name is its last segment, for either separator', async () => {
    const page = await bootLibrary();

    assert.deepEqual(page.evaluate(`[
        extractDisplayName('Batman/Batman #001.cbz'),
        extractDisplayName('C:\\\\Comics\\\\Saga.cbz'),
        extractDisplayName('plain.cbz'),
        extractDisplayName('trailing/'),
        extractDisplayName('')
    ]`), ['Batman #001.cbz', 'Saga.cbz', 'plain.cbz', 'trailing/', '']);
});

test('a file\'s Actions menu id is unique per path and safe in a selector', async () => {
    const page = await bootLibrary();

    assert.equal(page.evaluate("getDropdownId('Mixed/dupe.cbz')"),
        'dropdown-' + Buffer.from('Mixed/dupe.cbz').toString('base64url'));
    assert.notEqual(page.evaluate("getDropdownId('Saga 001')"), page.evaluate("getDropdownId('Saga_001')"));
});

test('select-all does nothing without an open series page', async () => {
    const page = await bootLibrary();

    page.run("selectedFiles.add('Saga/a.cbz'); toggleSelectAll(false)");

    assert.deepEqual(page.evaluate('[...selectedFiles]'), ['Saga/a.cbz']);
    assert.deepEqual(page.errors, []);
});
