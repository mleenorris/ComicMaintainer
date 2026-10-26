'use strict';

// js/app/modals.js — dialog accessibility: roles and labels, initial focus,
// the Tab focus trap, Escape via the dialog's own close button, nested
// dialogs and focus restoration.

const test = require('node:test');
const assert = require('node:assert/strict');
const { boot, press } = require('./support/core-helpers');

const closeOnClick = id => `onclick="document.getElementById('${id}').classList.remove('active')"`;

// Extra dialogs, added before the scripts run so initModalAccessibility() sees
// them: one without role/aria attributes, one whose only close button sits in
// the footer, and one with nothing focusable in it.
const TEST_DIALOGS = `
    <button type="button" id="opener">Open</button>
    <div class="modal" id="dialogA">
        <div class="modal-content">
            <div class="modal-header"><h2>A</h2><button class="close-btn" ${closeOnClick('dialogA')}>&times;</button></div>
            <div class="modal-body">
                <input id="dialogAName"><button type="button" id="dialogAOk">OK</button>
                <button type="button" id="dialogAMore" hidden>More</button>
            </div>
        </div>
    </div>
    <div class="modal" id="dialogB" role="alertdialog" aria-modal="false">
        <div class="modal-content">
            <div class="modal-body"><button type="button" id="dialogBYes">Yes</button></div>
            <div class="modal-footer"><button class="close-btn" aria-label="Dismiss" ${closeOnClick('dialogB')}>Done</button></div>
        </div>
    </div>
    <div class="modal" id="dialogEmpty"><div class="modal-content"><p>Working…</p></div></div>`;

const addTestDialogs = window => window.document.body.insertAdjacentHTML('beforeend', TEST_DIALOGS);

const bootWithDialogs = (options = {}) => boot({ hash: '#/library', beforeScripts: addTestDialogs, ...options });

async function open(page, id) {
    page.$(`#${id}`).classList.add('active');
    await page.settle();
}

const focused = page => page.document.activeElement;
const stackIds = page => page.evaluate('modalA11y.stack.map(entry => entry.modal.id)');

test('every dialog is exposed as a hidden modal dialog with a named close button', async () => {
    const page = await bootWithDialogs();

    for (const modal of page.$$('.modal')) {
        assert.ok(['dialog', 'alertdialog'].includes(modal.getAttribute('role')), modal.id);
        assert.equal(modal.getAttribute('aria-hidden'), 'true', modal.id);
        for (const button of modal.querySelectorAll('.close-btn')) {
            assert.ok(button.getAttribute('aria-label'), `${modal.id} close button is named`);
        }
    }
    assert.equal(page.$('#dialogA').getAttribute('role'), 'dialog');
    assert.equal(page.$('#dialogA').getAttribute('aria-modal'), 'true');
    assert.equal(page.$('#dialogA .close-btn').getAttribute('aria-label'), 'Close');
    assert.equal(page.$('#dialogB').getAttribute('role'), 'alertdialog', 'an explicit role is kept');
    assert.equal(page.$('#dialogB').getAttribute('aria-modal'), 'false');
    assert.equal(page.$('#dialogB .close-btn').getAttribute('aria-label'), 'Dismiss');
});

test('opening a dialog focuses its first control other than the close button; Escape closes it and restores focus', async () => {
    const page = await bootWithDialogs();
    page.$('#opener').focus();

    page.run('openKeyboardShortcutsModal()');
    await page.settle();
    const modal = page.$('#keyboardShortcutsModal');
    assert.equal(modal.getAttribute('aria-hidden'), 'false');
    assert.equal(focused(page), page.$('#keyboardShortcutsModal .modal-footer .btn'));

    const escape = press(page, 'Escape');
    assert.equal(escape.defaultPrevented, true);
    assert.equal(modal.classList.contains('active'), false, 'closed through its own close button');
    await page.settle();
    assert.equal(modal.getAttribute('aria-hidden'), 'true');
    assert.equal(focused(page), page.$('#opener'));
    assert.deepEqual(stackIds(page), []);
});

test('Tab and Shift+Tab wrap around inside the open dialog', async () => {
    const page = await bootWithDialogs();
    await open(page, 'dialogA');
    const [close, name, ok] = ['#dialogA .close-btn', '#dialogAName', '#dialogAOk'].map(selector => page.$(selector));
    assert.equal(focused(page), name);

    ok.focus();
    assert.equal(press(page, 'Tab').defaultPrevented, true);
    assert.equal(focused(page), close, 'Tab on the last shown control wraps to the first');

    assert.equal(press(page, 'Tab', { shiftKey: true }).defaultPrevented, true);
    assert.equal(focused(page), ok, 'Shift+Tab on the first control wraps to the last');

    name.focus();
    assert.equal(press(page, 'Tab').defaultPrevented, false, 'Tab between inner controls is left to the browser');
    assert.equal(press(page, 'Tab', { shiftKey: true }).defaultPrevented, false);

    page.$('#opener').focus();
    assert.equal(press(page, 'Tab').defaultPrevented, true);
    assert.equal(focused(page), close, 'focus outside the dialog is pulled back in');
    page.$('#opener').focus();
    press(page, 'Tab', { shiftKey: true });
    assert.equal(focused(page), ok);

    assert.equal(press(page, 'Enter').defaultPrevented, false, 'other keys pass through');
});

test('a dialog with nothing focusable takes focus itself, swallows Tab and ignores Escape', async () => {
    const page = await bootWithDialogs();
    await open(page, 'dialogEmpty');
    const modal = page.$('#dialogEmpty');

    assert.equal(modal.getAttribute('tabindex'), '-1');
    assert.equal(focused(page), modal);
    assert.equal(press(page, 'Tab').defaultPrevented, true);

    const escape = press(page, 'Escape');
    assert.equal(escape.defaultPrevented, false, 'no close button to press');
    assert.equal(modal.classList.contains('active'), true);
});

test('nested dialogs unwind one at a time, each restoring the focus it took over', async () => {
    const page = await bootWithDialogs();
    page.$('#opener').focus();
    await open(page, 'dialogA');
    page.$('#dialogAOk').focus();
    await open(page, 'dialogB');

    assert.deepEqual(stackIds(page), ['dialogA', 'dialogB']);
    assert.equal(focused(page), page.$('#dialogBYes'));

    assert.equal(press(page, 'Escape').defaultPrevented, true, 'the footer close button is used');
    await page.settle();
    assert.equal(page.$('#dialogB').classList.contains('active'), false);
    assert.equal(page.$('#dialogA').classList.contains('active'), true);
    assert.equal(focused(page), page.$('#dialogAOk'));

    press(page, 'Escape');
    await page.settle();
    assert.equal(focused(page), page.$('#opener'));
    assert.deepEqual(stackIds(page), []);
});

test('focus the opener already placed inside the dialog is kept', async () => {
    const page = await bootWithDialogs();
    page.$('#dialogA').classList.add('active');
    page.$('#dialogAOk').focus();
    await page.settle();

    assert.equal(focused(page), page.$('#dialogAOk'));
});

test('a dialog closed before its deferred focus runs is left alone', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await bootWithDialogs();
    page.$('#opener').focus();

    page.$('#dialogA').classList.add('active');
    await page.settle(1);
    assert.deepEqual(stackIds(page), ['dialogA']);
    page.$('#dialogA').classList.remove('active');
    await page.settle(1);
    t.mock.timers.tick(0);

    assert.equal(focused(page), page.$('#opener'));
    assert.deepEqual(stackIds(page), []);
});

test('closing a dialog does not steal focus the user already moved elsewhere', async () => {
    const page = await bootWithDialogs();
    page.$('#opener').focus();
    await open(page, 'dialogA');

    page.$('#headerSearchInput').focus();
    page.$('#dialogA').classList.remove('active');
    await page.settle();
    assert.equal(focused(page), page.$('#headerSearchInput'));

    // Focus dropped to <body> (e.g. the focused control was removed) is put
    // back on the opener, unless the opener is gone too.
    page.$('#opener').focus();
    await open(page, 'dialogA');
    page.$('#dialogAName').blur();
    page.$('#dialogA').classList.remove('active');
    await page.settle();
    assert.equal(focused(page), page.$('#opener'));

    await open(page, 'dialogA');
    page.$('#dialogAName').blur();
    page.$('#opener').remove();
    page.$('#dialogA').classList.remove('active');
    await page.settle();
    assert.equal(focused(page), page.document.body, 'nothing to restore focus to');
    assert.deepEqual(stackIds(page), []);
});

test('dialogs shown with an inline display or the .show class are tracked too', async () => {
    const page = await bootWithDialogs();
    const modal = page.$('#dialogA');

    modal.style.display = 'flex';
    modal.style.zIndex = '10';
    await page.settle();
    assert.deepEqual(stackIds(page), ['dialogA']);
    assert.equal(modal.getAttribute('aria-hidden'), 'false');

    modal.classList.add('busy');
    await page.settle();
    assert.deepEqual(stackIds(page), ['dialogA'], 'an unrelated change while open is ignored');

    modal.style.display = '';
    await page.settle();
    assert.deepEqual(stackIds(page), []);
    assert.equal(modal.getAttribute('aria-hidden'), 'true');

    modal.style.zIndex = '';
    await page.settle();
    assert.deepEqual(stackIds(page), [], 'an unrelated change while closed is ignored');

    modal.classList.add('show');
    await page.settle();
    assert.deepEqual(stackIds(page), ['dialogA']);
});

test('a tracked dialog removed from the page no longer owns the keyboard', async () => {
    const page = await bootWithDialogs();
    await open(page, 'dialogA');
    page.$('#dialogA').remove();
    page.$('#opener').focus();

    assert.equal(press(page, 'Tab').defaultPrevented, false);
    assert.equal(press(page, 'Escape').defaultPrevented, false);
});

test('a dialog that is open when the page loads is set up as open', async () => {
    const page = await boot({
        hash: '#/library',
        beforeScripts(window) {
            addTestDialogs(window);
            window.document.getElementById('dialogA').classList.add('active');
        }
    });
    assert.equal(page.$('#dialogA').getAttribute('aria-hidden'), 'false');
    assert.equal(focused(page), page.$('#dialogAName'));

    page.run('initModalAccessibility()');
    assert.deepEqual(stackIds(page), ['dialogA'], 'initialising again does not track it twice');

    press(page, 'Escape');
    await page.settle();
    assert.deepEqual(stackIds(page), []);
});

test('focus that was on an SVG control is not restored', async () => {
    const page = await bootWithDialogs({
        beforeScripts(window) {
            addTestDialogs(window);
            window.document.body.insertAdjacentHTML('beforeend', '<svg id="svgControl" tabindex="0"></svg>');
        }
    });
    page.$('#svgControl').focus();
    assert.equal(focused(page), page.$('#svgControl'));

    await open(page, 'dialogA');
    assert.equal(page.evaluate('modalA11y.stack[0].previousFocus'), null);
    page.$('#dialogAName').blur();
    press(page, 'Escape');
    await page.settle();
    assert.deepEqual(stackIds(page), []);
    assert.equal(focused(page), page.document.body);
});

test('closing a dialog that was never tracked only marks it hidden', async () => {
    const page = await bootWithDialogs();
    page.$('#opener').focus();
    page.$('#dialogA').setAttribute('aria-hidden', 'false');

    page.run("handleModalClosed(document.getElementById('dialogA'))");
    assert.equal(page.$('#dialogA').getAttribute('aria-hidden'), 'true');
    assert.equal(focused(page), page.$('#opener'));
});

test('a page without dialogs sets nothing up', async () => {
    const page = await boot();
    page.$$('.modal').forEach(modal => modal.remove());

    page.run('initModalAccessibility()');
    assert.deepEqual(page.errors, []);
});
