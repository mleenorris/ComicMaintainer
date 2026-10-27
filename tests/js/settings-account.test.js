'use strict';

// Settings menu → Change Password (js/app/settings.js): client-side validation,
// the request, the pending/success/failure states and closing the dialog.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { bootSettings, callsTo } = require('./support/settings-helpers');

const dialog = page => ({
    open: page.$('#changePasswordModal').classList.contains('active'),
    error: page.$('#changePasswordError').classList.contains('show') ? page.$('#changePasswordError').textContent : null,
    success: page.$('#changePasswordSuccess').classList.contains('show') ? page.$('#changePasswordSuccess').textContent : null,
    button: `${page.$('#changePasswordBtn').textContent}${page.$('#changePasswordBtn').disabled ? ' (disabled)' : ''}`,
    fields: ['#currentPassword', '#newPassword', '#confirmNewPassword'].map(selector => page.$(selector).value)
});

function fill(page, current, next, confirm) {
    page.input('#currentPassword', current);
    page.input('#newPassword', next);
    page.input('#confirmNewPassword', confirm);
}

test('Change Password opens a clean form and validates it before sending anything', async () => {
    const page = await bootSettings();

    // Leftovers from a previous visit are cleared when the dialog opens.
    fill(page, 'stale', 'stale-new', 'stale-new');
    page.$('#changePasswordError').textContent = 'old error';
    page.$('#changePasswordError').classList.add('show');
    page.$('#changePasswordSuccess').textContent = 'old success';
    page.$('#changePasswordSuccess').classList.add('show');

    page.click('#settingsMenuToggle');
    page.click('#settingsDropdownMenu button[onclick^="openChangePasswordModal()"]');
    assert.equal(page.$('#settingsDropdownMenu').classList.contains('show'), false);
    assert.deepEqual(dialog(page), { open: true, error: null, success: null, button: 'Change Password', fields: ['', '', ''] });
    assert.equal(page.$('#changePasswordError').textContent, '');
    assert.equal(page.$('#changePasswordSuccess').textContent, '');

    const since = page.fetchCalls.length;
    for (const [fields, error] of [
        [['', '', ''], 'All fields are required'],
        [['current-pass', '', ''], 'All fields are required'],
        [['current-pass', 'new-password', ''], 'All fields are required'],
        [['', 'new-password', 'new-password'], 'All fields are required'],
        [['current-pass', 'short7!', 'short7!'], 'New password must be at least 8 characters long'],
        [['current-pass', 'new-password', 'new-passwort'], 'New passwords do not match']
    ]) {
        fill(page, ...fields);
        page.click('#changePasswordBtn');
        await page.settle(2);
        assert.deepEqual(dialog(page), { open: true, error, success: null, button: 'Change Password', fields }, fields.join('|'));
    }
    assert.deepEqual(callsTo(page, 'POST /api/auth/change-password', since), []);

    page.click('#changePasswordModal .close-btn');
    assert.equal(dialog(page).open, false);
    page.run('openChangePasswordModal()');
    page.click('#changePasswordModal .modal-footer button[onclick="closeChangePasswordModal()"]');
    assert.equal(dialog(page).open, false);
});

test('a successful change is confirmed and the dialog closes two seconds later', async t => {
    const page = await bootSettings();
    t.mock.timers.enable({ apis: ['setTimeout'] });

    const pending = deferred();
    page.route('POST /api/auth/change-password', () => pending.promise);
    page.run('openChangePasswordModal()');
    fill(page, 'old-password', 'new-password-1', 'new-password-1');

    page.click('#changePasswordBtn');
    await page.settle(2);
    assert.deepEqual(dialog(page), {
        open: true, error: null, success: null, button: 'Changing... (disabled)',
        fields: ['old-password', 'new-password-1', 'new-password-1']
    });
    const [call] = callsTo(page, 'POST /api/auth/change-password');
    assert.deepEqual(call.body, { currentPassword: 'old-password', newPassword: 'new-password-1' });
    assert.match(call.headers.authorization, /^Bearer /);
    assert.equal(call.headers['content-type'], 'application/json');

    pending.resolve({ message: 'Password changed successfully' });
    await page.settle();
    assert.deepEqual(dialog(page), {
        open: true, error: null, success: 'Password changed successfully!', button: 'Change Password', fields: ['', '', '']
    });

    t.mock.timers.tick(1999);
    assert.equal(dialog(page).open, true);
    t.mock.timers.tick(1);
    assert.equal(dialog(page).open, false);
});

test('submitting the form with Enter changes the password too', async () => {
    const page = await bootSettings({ routes: { 'POST /api/auth/change-password': { message: 'Password changed successfully' } } });
    page.run('openChangePasswordModal()');
    fill(page, 'old-password', 'new-password-2', 'new-password-2');

    const form = page.$('#changePasswordForm');
    const submit = new page.window.Event('submit', { bubbles: true, cancelable: true });
    form.dispatchEvent(submit);
    assert.equal(submit.defaultPrevented, true, 'the page does not navigate');
    await page.settle();

    assert.deepEqual(callsTo(page, 'POST /api/auth/change-password').map(c => c.body), [
        { currentPassword: 'old-password', newPassword: 'new-password-2' }
    ]);
    assert.equal(dialog(page).success, 'Password changed successfully!');
});

test('a rejected or failed change explains why and lets the user retry', async () => {
    const page = await bootSettings();
    page.run('openChangePasswordModal()');
    const generic = 'An error occurred. Please try again.';

    for (const [response, error] of [
        [respond(400, { error: 'Current password is incorrect' }), 'Current password is incorrect'],
        [respond(400, {}), 'Failed to change password'],
        [respond(500, 'Internal Server Error'), generic],
        [() => { throw new TypeError('Failed to fetch'); }, generic]
    ]) {
        page.route('POST /api/auth/change-password', response);
        fill(page, 'old-password', 'new-password-1', 'new-password-1');
        page.click('#changePasswordBtn');
        await page.settle();
        assert.deepEqual(dialog(page), {
            open: true, error, success: null, button: 'Change Password',
            fields: ['old-password', 'new-password-1', 'new-password-1']
        }, error);
    }
    assert.equal(page.consoleMessages.filter(m => m.type === 'error' && m.args[0] === 'Error changing password:').length, 2);

    // An expired session goes to the login page.
    page.route('POST /api/auth/change-password', 401);
    page.click('#changePasswordBtn');
    await page.settle();
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
    assert.deepEqual(dialog(page), {
        open: true, error: null, success: null, button: 'Change Password',
        fields: ['old-password', 'new-password-1', 'new-password-1']
    });
});
