'use strict';

// js/app/modals.js — library keyboard shortcuts: "/" search, "?" help,
// "g" + letter navigation (with its timeout), "r" refresh, "f" filter menu,
// Escape in the search box, and the guards for typing, modifier chords,
// open dialogs and controls hidden on the current page.

const test = require('node:test');
const assert = require('node:assert/strict');
const { boot, messages, press, requests } = require('./support/core-helpers');

const useMockTimers = t => t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
const hash = page => page.window.location.hash;

/** Press keys on <body> (no control focused) and let any navigation run. */
async function keys(page, ...sequence) {
    const events = sequence.map(key => (typeof key === 'string' ? press(page, key, {}, page.document.body) : press(page, key.key, key, page.document.body)));
    await page.settle();
    return events;
}

test('"/" focuses and selects the search box where it is shown', async () => {
    const page = await boot({ hash: '#/library' });
    page.$('#headerSearchInput').value = 'saga';

    const [slash] = await keys(page, '/');
    assert.equal(slash.defaultPrevented, true);
    const input = page.$('#headerSearchInput');
    assert.equal(page.document.activeElement, input);
    assert.deepEqual([input.selectionStart, input.selectionEnd], [0, 4]);

    const overview = await boot({ hash: '#/home' });
    const [inert] = await keys(overview, '/');
    assert.equal(inert.defaultPrevented, false, 'the search box is hidden on the overview');
    assert.equal(overview.document.activeElement, overview.document.body);
});

test('"?" opens the shortcut help on any page, and Escape closes it', async () => {
    const page = await boot({ hash: '#/home' });

    const [question] = await keys(page, '?');
    assert.equal(question.defaultPrevented, true);
    assert.equal(page.$('#keyboardShortcutsModal').classList.contains('active'), true);

    press(page, 'Escape');
    assert.equal(page.$('#keyboardShortcutsModal').classList.contains('active'), false);
});

test('"g" followed by a letter moves between the pages', async () => {
    const page = await boot({ hash: '#/home' });

    const [, toLibrary] = await keys(page, 'g', 'l');
    assert.equal(toLibrary.defaultPrevented, true);
    assert.equal(hash(page), '#/library');

    await keys(page, 'G', 'S');
    assert.equal(hash(page), '#/settings', 'both keys are case-insensitive');

    await keys(page, 'g', 'j');
    assert.equal(hash(page), '#/jobs');

    await keys(page, 'g', 'h');
    assert.equal(hash(page), '#/home');
    assert.equal(page.evaluate('currentView'), 'home');
});

test('an unknown second key cancels the "g" sequence, and a bare letter does nothing', async () => {
    const page = await boot({ hash: '#/home' });

    const [, unknown, bare] = await keys(page, 'g', 'x', 'l');
    assert.equal(unknown.defaultPrevented, false);
    assert.equal(bare.defaultPrevented, false);
    assert.equal(hash(page), '#/home');
    assert.equal(page.evaluate('pendingShortcutPrefix'), null);
});

test('"g j" is only a shortcut for administrators', async () => {
    const page = await boot({ hash: '#/home', user: { canAdminister: false } });

    const [, jobs] = await keys(page, 'g', 'j');
    assert.equal(jobs.defaultPrevented, false);
    assert.equal(hash(page), '#/home');
});

test('a "g" left unfinished expires after 1.2 seconds, and "g g" cancels the sequence', async t => {
    useMockTimers(t);
    const page = await boot({ hash: '#/home' });

    await keys(page, 'g');
    t.mock.timers.tick(1199);
    await keys(page, 'l');
    assert.equal(hash(page), '#/library', 'still inside the 1.2 s window');

    await keys(page, 'g');
    t.mock.timers.tick(1200);
    assert.equal(page.evaluate('pendingShortcutPrefix'), null);
    await keys(page, 'h');
    assert.equal(hash(page), '#/library', 'the expired prefix no longer applies');

    const [, second, third] = await keys(page, 'g', 'g', 'h');
    assert.equal(second.defaultPrevented, false);
    assert.equal(third.defaultPrevented, false);
    assert.equal(hash(page), '#/library', 'a second "g" ends the sequence instead of starting a new one');
    assert.equal(page.evaluate('pendingShortcutPrefixTimer'), null);
});

test('"r" refreshes and "f" opens the filter menu on the library, but not on the overview', async () => {
    const page = await boot({ hash: '#/library' });
    const refreshesBefore = requests(page, 'GET', '/api/files/series').filter(call => call.query.refresh === 'true').length;

    const [refresh, filter] = await keys(page, 'r', 'f');
    assert.equal(refresh.defaultPrevented, true);
    assert.deepEqual(messages(page).map(message => message.text), ['Refreshing file list...']);
    assert.equal(requests(page, 'GET', '/api/files/series').filter(call => call.query.refresh === 'true').length, refreshesBefore + 1);
    assert.equal(filter.defaultPrevented, true);
    assert.equal(page.$('#headerFilterMenu').classList.contains('show'), true);

    page.$('#headerFilterMenu').classList.remove('show');
    await keys(page, 'R', 'F');
    assert.equal(messages(page).length, 2, 'upper case works too');
    assert.equal(page.$('#headerFilterMenu').classList.contains('show'), true);

    const overview = await boot({ hash: '#/home' });
    const [inertRefresh, inertFilter] = await keys(overview, 'r', 'f');
    assert.equal(inertRefresh.defaultPrevented, false);
    assert.equal(inertFilter.defaultPrevented, false);
    assert.deepEqual(messages(overview), []);
    assert.equal(overview.$('#headerFilterMenu').classList.contains('show'), false);

    page.$('#headerFilterToggle').remove();
    const [missing] = await keys(page, 'f');
    assert.equal(missing.defaultPrevented, false);
});

test('shortcuts are ignored while typing, with modifier keys and while a dialog is open', async () => {
    const page = await boot({ hash: '#/home' });
    page.document.body.insertAdjacentHTML('beforeend', '<textarea id="notes"></textarea><select id="pick"><option>a</option></select><div id="editor"></div>');
    // jsdom does not implement isContentEditable.
    Object.defineProperty(page.$('#editor'), 'isContentEditable', { value: true });

    for (const selector of ['#notes', '#pick', '#editor', '#headerSearchInput']) {
        press(page, 'g', {}, page.$(selector));
        press(page, 'l', {}, page.$(selector));
        assert.equal(press(page, '?', {}, page.$(selector)).defaultPrevented, false, selector);
    }
    for (const modifier of ['ctrlKey', 'metaKey', 'altKey']) {
        assert.equal(press(page, '?', { [modifier]: true }, page.document.body).defaultPrevented, false, modifier);
    }
    await page.settle();
    assert.equal(hash(page), '#/home');
    assert.equal(page.$('#keyboardShortcutsModal').classList.contains('active'), false);

    // Keys dispatched at the document itself (no element target) still count.
    const atDocument = press(page, '?', {}, page.document);
    assert.equal(atDocument.defaultPrevented, true);
    await page.settle();

    const [, blocked] = await keys(page, 'g', 'l');
    assert.equal(blocked.defaultPrevented, false, 'the open help dialog owns the keyboard');
    assert.equal(hash(page), '#/home');
});

test('Escape in the search box clears it, and only claims the key when there was text', async () => {
    const page = await boot({ hash: '#/library' });
    const input = page.$('#headerSearchInput');
    input.focus();
    input.value = 'saga';

    assert.equal(press(page, 'Escape').defaultPrevented, true);
    assert.equal(input.value, '');
    assert.equal(press(page, 'Escape').defaultPrevented, false);
});

test('the help dialog helpers and the shortcut setup tolerate missing markup', async () => {
    const page = await boot({ hash: '#/library' });
    page.$('#keyboardShortcutsModal').remove();
    page.run('openKeyboardShortcutsModal(); closeKeyboardShortcutsModal();');

    page.$('#headerSearchInput').remove();
    page.run('initLibraryKeyboardShortcuts()');
    assert.deepEqual(page.errors, []);
});
