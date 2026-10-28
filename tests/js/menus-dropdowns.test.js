'use strict';

// js/app/menus.js: the per-file Actions dropdown (portaled to <body> and
// positioned against the viewport), the toolbar action dropdowns, and closing
// every kind of menu on an outside click.

const test = require('node:test');
const assert = require('node:assert/strict');
const { bootApp } = require('./support/app');

const FILES = [
    { name: 'Issue #1.cbz', relative_path: 'Series A/Issue #1.cbz', size: 2048, modified: 1700000000, renamed: true, normalized: true },
    { name: 'Issue 2.cbz', relative_path: 'Series A/Issue 2.cbz', size: 4096, modified: 1700000100 }
];
const MENU_A = 'dropdown-Series_A_Issue__1_cbz';
const MENU_B = 'dropdown-Series_A_Issue_2_cbz';

async function bootLibrary() {
    const page = await bootApp({
        hash: '#/library',
        routes: { 'GET /api/files/series': { series: [], total_series: 0 } }
    });
    // File rows exactly as the file list renders them, inside a transformed
    // container like the series issue cards that made portaling necessary.
    const container = page.document.createElement('div');
    container.id = 'testRows';
    container.style.transform = 'translateY(-2px)';
    container.insertAdjacentHTML('beforeend', FILES.map(file => page.run('renderFileRow')(file)).join(''));
    page.document.body.appendChild(container);
    const rows = [...container.querySelectorAll('.file-actions-dropdown')];
    return { page, rows };
}

function setViewport(page, width, height) {
    Object.defineProperty(page.window, 'innerWidth', { value: width, configurable: true });
    Object.defineProperty(page.window, 'innerHeight', { value: height, configurable: true });
}

/** Give a row's toggle and menu fixed layout boxes; returns how the menu was measured. */
function layout(row, { button, menu }) {
    const toggle = row.querySelector('.dropdown-toggle');
    const dropdown = row.querySelector('.dropdown-menu');
    toggle.getBoundingClientRect = () => ({ ...button, width: button.right - button.left, height: button.bottom - button.top });
    const measured = [];
    dropdown.getBoundingClientRect = () => {
        measured.push({ visibility: dropdown.style.visibility, display: dropdown.style.display, portaled: dropdown.parentNode === row.ownerDocument.body });
        return { top: 0, left: 0, right: menu.width, bottom: menu.height, width: menu.width, height: menu.height };
    };
    return { toggle, dropdown, measured };
}

const placeholders = page => {
    const walker = page.document.createTreeWalker(page.document, page.window.NodeFilter.SHOW_COMMENT);
    const found = [];
    while (walker.nextNode()) if (walker.currentNode.data === 'dropdown-placeholder') found.push(walker.currentNode);
    return found;
};

const errorsSince = (page, since) => page.consoleMessages.slice(since)
    .filter(message => message.type === 'error')
    .map(message => message.args);

const state = dropdown => ({
    shown: dropdown.classList.contains('show'),
    above: dropdown.classList.contains('show-above'),
    top: dropdown.style.top,
    left: dropdown.style.left
});

test('a file\'s Actions menu opens below its button, moved to <body> while open', async () => {
    const { page, rows } = await bootLibrary();
    setViewport(page, 1200, 800);
    const { toggle, dropdown, measured } = layout(rows[0], { button: { top: 100, bottom: 130, left: 900, right: 980 }, menu: { width: 200, height: 300 } });
    assert.equal(dropdown.id, MENU_A);

    page.click(toggle);
    assert.deepEqual(state(dropdown), { shown: true, above: false, top: '132px', left: '780px' });
    assert.equal(dropdown.parentNode, page.document.body);
    const [placeholder, ...others] = placeholders(page);
    assert.deepEqual(others, []);
    assert.equal(placeholder.parentNode, rows[0], 'a placeholder keeps its place in the row');
    assert.equal(placeholder.previousElementSibling, toggle);
    // It is measured invisibly, already in <body>, then shown normally.
    assert.deepEqual(measured, [{ visibility: 'hidden', display: 'block', portaled: true }]);
    assert.equal(dropdown.style.visibility, '');
    assert.equal(dropdown.style.display, '');

    // The click that opened it does not also reach the outside-click handler.
    await page.settle(2);
    assert.equal(state(dropdown).shown, true);

    page.click(toggle);
    assert.deepEqual(state(dropdown), { shown: false, above: false, top: '', left: '' });
    assert.equal(dropdown.parentNode, rows[0], 'closed menus go back to their row');
    assert.equal(toggle.nextElementSibling, dropdown);
    assert.deepEqual(placeholders(page), []);
    assert.deepEqual(page.errors, []);
});

test('the Actions menu flips above its button and stays inside the viewport', async () => {
    const { page, rows } = await bootLibrary();
    const cases = [
        ['no room below', [1200, 800], { top: 670, bottom: 700, left: 900, right: 980 }, { width: 200, height: 300 },
            { shown: true, above: true, top: '368px', left: '780px' }],
        ['bottom half, more room above', [1200, 1000], { top: 600, bottom: 630, left: 900, right: 980 }, { width: 200, height: 100 },
            { shown: true, above: true, top: '498px', left: '780px' }],
        ['bottom half, as much room below', [1200, 1000], { top: 480, bottom: 520, left: 900, right: 980 }, { width: 200, height: 100 },
            { shown: true, above: false, top: '522px', left: '780px' }],
        ['top half', [1200, 800], { top: 100, bottom: 130, left: 900, right: 980 }, { width: 200, height: 300 },
            { shown: true, above: false, top: '132px', left: '780px' }],
        ['would overflow on the left', [1200, 800], { top: 100, bottom: 130, left: 70, right: 150 }, { width: 200, height: 300 },
            { shown: true, above: false, top: '132px', left: '10px' }],
        ['exactly at the left margin', [1200, 800], { top: 100, bottom: 130, left: 130, right: 210 }, { width: 200, height: 300 },
            { shown: true, above: false, top: '132px', left: '10px' }],
        ['would overflow on the right', [1000, 800], { top: 100, bottom: 130, left: 1020, right: 1100 }, { width: 200, height: 300 },
            { shown: true, above: false, top: '132px', left: '790px' }],
        // In a very narrow window (200 CSS px is an 800 px window at 400 %
        // zoom) keeping the menu off the right edge must not push the start
        // of its items past the left one.
        ['nearly as wide as the viewport', [200, 800], { top: 100, bottom: 130, left: 150, right: 190 }, { width: 195, height: 300 },
            { shown: true, above: false, top: '132px', left: '10px' }],
        ['as wide as the viewport less its margins', [200, 800], { top: 100, bottom: 130, left: 10, right: 50 }, { width: 180, height: 300 },
            { shown: true, above: false, top: '132px', left: '10px' }],
        ['wider than the viewport', [140, 800], { top: 100, bottom: 130, left: 100, right: 130 }, { width: 150, height: 300 },
            { shown: true, above: false, top: '132px', left: '10px' }]
    ];
    for (const [name, [width, height], button, menu, expected] of cases) {
        setViewport(page, width, height);
        const { toggle, dropdown } = layout(rows[0], { button, menu });
        page.click(toggle);
        assert.deepEqual(state(dropdown), expected, name);
        page.click(toggle);
        assert.deepEqual(state(dropdown), { shown: false, above: false, top: '', left: '' }, `${name} (closed)`);
    }
});

test('only one Actions menu is open at a time', async () => {
    const { page, rows } = await bootLibrary();
    setViewport(page, 1200, 800);
    const a = layout(rows[0], { button: { top: 100, bottom: 130, left: 900, right: 980 }, menu: { width: 200, height: 300 } });
    const b = layout(rows[1], { button: { top: 200, bottom: 230, left: 900, right: 980 }, menu: { width: 200, height: 300 } });

    page.click(a.toggle);
    page.click(b.toggle);
    assert.deepEqual(state(a.dropdown), { shown: false, above: false, top: '', left: '' });
    assert.equal(a.dropdown.parentNode, rows[0]);
    assert.deepEqual(state(b.dropdown), { shown: true, above: false, top: '232px', left: '780px' });
    assert.equal(b.dropdown.parentNode, page.document.body);
    assert.equal(placeholders(page).length, 1);

    // Clicks inside the open (portaled) menu or on its row's action area keep it open.
    page.click(b.dropdown.querySelector('.dropdown-divider'));
    page.click(rows[1]);
    assert.equal(state(b.dropdown).shown, true);

    // Anywhere else closes it and returns it to its row.
    page.click(page.document.body);
    assert.equal(state(b.dropdown).shown, false);
    assert.equal(b.dropdown.parentNode, rows[1]);
    assert.deepEqual(placeholders(page), []);
});

test('portaling copes with repeated, unanchored and unknown menus', async () => {
    const { page, rows } = await bootLibrary();
    const a = rows[0].querySelector('.dropdown-menu');

    // Moving the same menu twice keeps a single placeholder.
    page.run('portalDropdownToBody')(a);
    page.run('portalDropdownToBody')(a);
    assert.equal(placeholders(page).length, 1);
    page.run('closeAllDropdowns()');
    assert.equal(a.parentNode, rows[0]);

    // If the row was re-rendered while the menu was open, closing still hides
    // it and clears its position.
    setViewport(page, 1200, 800);
    const { toggle } = layout(rows[0], { button: { top: 100, bottom: 130, left: 900, right: 980 }, menu: { width: 200, height: 300 } });
    page.click(toggle);
    placeholders(page)[0].remove();
    page.run('closeAllDropdowns()');
    assert.deepEqual(state(a), { shown: false, above: false, top: '', left: '' });
    const b = layout(rows[1], { button: { top: 200, bottom: 230, left: 900, right: 980 }, menu: { width: 200, height: 300 } });
    page.click(b.toggle);
    assert.deepEqual(state(b.dropdown), { shown: true, above: false, top: '232px', left: '780px' });
    page.run('closeAllDropdowns()');

    // Opened from somewhere other than its toggle (here its row's action
    // area), the menu is shown but not positioned.
    const event = { stopPropagation() {}, target: rows[1] };
    page.run('toggleDropdown')(event, FILES[1].relative_path);
    assert.deepEqual(state(b.dropdown), { shown: true, above: false, top: '', left: '' });
    assert.equal(b.dropdown.parentNode, page.document.body);

    const logged = page.consoleMessages.length;
    page.run('toggleDropdown')({ stopPropagation() {}, target: page.document.body }, 'Missing/file #9.cbz');
    assert.deepEqual(errorsSince(page, logged), [
        ['Dropdown not found for filepath:', 'Missing/file #9.cbz', 'ID:', 'dropdown-Missing_file__9_cbz']
    ]);
    assert.equal(state(b.dropdown).shown, true, 'an unknown menu leaves the open one alone');
    assert.equal(page.$(`#${MENU_B}`), b.dropdown);
});

test('toolbar action dropdowns open one at a time', async () => {
    const { page } = await bootLibrary();
    const toggleFor = id => page.$(`button[onclick="toggleActionDropdown(event, '${id}')"]`);
    const open = () => page.$$('.action-dropdown-menu.show').map(menu => menu.id);

    page.click(toggleFor('processDropdown'));
    assert.deepEqual(open(), ['processDropdown']);
    await page.settle(2);
    assert.deepEqual(open(), ['processDropdown'], 'the opening click does not count as an outside click');

    page.click(toggleFor('renameDropdown'));
    assert.deepEqual(open(), ['renameDropdown']);
    page.click('#renameDropdown');
    assert.deepEqual(open(), ['renameDropdown'], 'clicks inside keep it open');
    page.click(toggleFor('renameDropdown'));
    assert.deepEqual(open(), []);

    page.click(toggleFor('markDropdown'));
    page.run('closeAllActionDropdowns()');
    assert.deepEqual(open(), []);

    const logged = page.consoleMessages.length;
    page.run('toggleActionDropdown')({ stopPropagation() {} }, 'noSuchDropdown');
    assert.deepEqual(errorsSince(page, logged), [['Action dropdown not found:', 'noSuchDropdown']]);
});

test('an outside click closes every kind of open menu', async () => {
    const { page, rows } = await bootLibrary();
    setViewport(page, 1200, 800);
    const fileMenu = layout(rows[0], { button: { top: 100, bottom: 130, left: 900, right: 980 }, menu: { width: 200, height: 300 } });
    const menus = [
        ['settings', () => page.click('#settingsMenuToggle'), '#settingsDropdownMenu', '#settingsDropdownMenu', '#settingsMenuToggle'],
        ['header filter', () => page.click('#headerFilterToggle'), '#headerFilterMenu', '#headerFilterMenu', '#headerFilterToggle'],
        ['header sort', () => page.click('#headerSortToggle'), '#headerSortMenu', '#headerSortMenu', '#headerSortToggle'],
        ['toolbar action', () => page.click(`button[onclick="toggleActionDropdown(event, 'normalizeDropdown')"]`), '#normalizeDropdown', '#normalizeDropdown'],
        ['file actions', () => page.click(fileMenu.toggle), `#${MENU_A}`, `#${MENU_A} .dropdown-divider`]
    ];
    const isOpen = selector => page.$(selector).classList.contains('show');

    for (const [name, open, menu, inside, toggle] of menus) {
        open();
        await page.settle(2);
        assert.equal(isOpen(menu), true, `${name} opens`);
        if (toggle) assert.equal(page.$(toggle).getAttribute('aria-expanded'), 'true', `${name} aria-expanded`);
        page.click(inside);
        await page.settle(2);
        assert.equal(isOpen(menu), true, `${name} stays open on an inside click`);
        page.click('#messageContainer');
        await page.settle(2);
        assert.equal(isOpen(menu), false, `${name} closes on an outside click`);
        if (toggle) assert.equal(page.$(toggle).getAttribute('aria-expanded'), 'false', `${name} aria-expanded`);
    }

    // Opening a file menu closes the legacy filter dropdown some pages still have...
    page.document.body.insertAdjacentHTML('beforeend', '<div id="filterDropdownMenu" class="show"></div>');
    page.click(fileMenu.toggle);
    assert.equal(page.$('#filterDropdownMenu').classList.contains('show'), false);
    // ...and one outside click closes all of them at once.
    page.$('#filterDropdownMenu').classList.add('show');
    page.click('#settingsMenuToggle');
    page.click('#headerSortToggle');
    page.click(`button[onclick="toggleActionDropdown(event, 'metadataDropdown')"]`);
    assert.deepEqual(page.$$('.show').map(element => element.id).sort(), [MENU_A, 'filterDropdownMenu', 'headerSortMenu', 'metadataDropdown', 'settingsDropdownMenu'].sort());
    page.click(page.document.body);
    assert.deepEqual(page.$$('.show').map(element => element.id), []);
    assert.equal(fileMenu.dropdown.parentNode, rows[0]);

    // Clicking an open file menu's own row action area closes only the other menus.
    page.click(fileMenu.toggle);
    page.click('#headerFilterToggle');
    page.$('#filterDropdownMenu').classList.add('show');
    page.click(rows[0]);
    assert.deepEqual(page.$$('.show').map(element => element.id).sort(), [MENU_A, 'filterDropdownMenu'].sort());
    assert.deepEqual(page.errors, []);
});
