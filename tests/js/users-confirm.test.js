'use strict';

// The users page's Disable, Enable and Delete confirmation dialog
// (js/app/users.js), and the Users dialogs closing when the page is left.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { press } = require('./support/core-helpers');
const u = require('./support/users-helpers');

const focusedId = page => page.document.activeElement && page.document.activeElement.id;

function confirmView(page) {
    const submit = page.$('#userConfirmSubmit');
    return {
        title: page.$('#userConfirmModalTitle').textContent,
        message: u.text(page.$('#userConfirmMessage')),
        passwordShown: !page.$('#userConfirmCurrentPasswordGroup').hidden,
        button: submit.textContent,
        danger: submit.classList.contains('btn-danger'),
        primary: submit.classList.contains('btn-primary')
    };
}

test('Disable asks to confirm, needs no password and signs the account out', async () => {
    const page = await u.bootUsers();
    page.route('PUT /api/users/u-bob/active', call => {
        page.server.users = [u.ME, { ...u.BOB, isActive: call.body.isActive }];
        return { ...u.BOB, isActive: call.body.isActive };
    });

    await u.clickAction(page, 'u-bob', 'disable');
    assert.ok(u.isOpen(page, 'userConfirmModal'));
    assert.deepEqual(confirmView(page), {
        title: 'Disable Account',
        message: 'Disable bob (Bob Builder)? The account is signed out everywhere, its API key is revoked, and it cannot sign in until it is enabled again. Its reading progress, preferences and devices are kept.',
        passwordShown: false,
        button: 'Disable',
        danger: true,
        primary: false
    });
    assert.equal(focusedId(page), 'userConfirmCancel', 'the keyboard starts on Cancel');

    await u.submit(page, 'userConfirmSubmit');
    assert.deepEqual(u.userRequests(page, 'PUT'), [{ path: '/api/users/u-bob/active', body: { isActive: false } }]);
    assert.equal(u.isOpen(page, 'userConfirmModal'), false);
    assert.deepEqual(u.takeMessages(page), [{ type: 'success', text: 'Disabled the account bob. It is signed out everywhere.' }]);
    assert.equal(u.cardView(page, 'u-bob').status, 'Disabled');
    assert.equal(focusedId(page), 'user-account-u-bob-name');
});

test('Enable asks for the administrator\'s password', async () => {
    const page = await u.bootUsers({ users: [u.ME, { ...u.BOB, isActive: false, fullName: null }] });
    page.route('PUT /api/users/u-bob/active', call => {
        page.server.users = [u.ME, { ...u.BOB, isActive: call.body.isActive }];
        return u.BOB;
    });

    await u.clickAction(page, 'u-bob', 'enable');
    assert.deepEqual(confirmView(page), {
        title: 'Enable Account',
        message: 'Enable bob? The account can sign in again with its password. Its sessions and API key from before it was disabled stay revoked.',
        passwordShown: true,
        button: 'Enable',
        danger: false,
        primary: true
    });
    assert.equal(focusedId(page), 'userConfirmCurrentPassword');

    await u.submit(page, 'userConfirmSubmit');
    assert.equal(page.$('#userConfirmError').textContent, 'Enter your own password to confirm.');
    assert.deepEqual(u.userRequests(page, 'PUT'), []);

    u.fill(page, { userConfirmCurrentPassword: 'MyOwnPassw0rd' });
    await u.submit(page, 'userConfirmSubmit');
    assert.deepEqual(u.userRequests(page, 'PUT'), [{ path: '/api/users/u-bob/active', body: { isActive: true, currentPassword: 'MyOwnPassw0rd' } }]);
    assert.deepEqual(u.takeMessages(page), [{ type: 'success', text: 'Enabled the account bob.' }]);
    assert.equal(u.cardView(page, 'u-bob').status, 'Active');

    // The dialog is set up afresh for the next action.
    await u.clickAction(page, 'u-bob', 'disable');
    assert.equal(confirmView(page).passwordShown, false);
    assert.equal(page.$('#userConfirmCurrentPassword').value, '');
});

test('Enable through Authelia sends no password', async () => {
    const page = await u.bootUsers({ users: [u.ME, { ...u.BOB, isActive: false }], user: { authMethod: 'authelia' } });
    page.route('PUT /api/users/u-bob/active', u.BOB);

    await u.clickAction(page, 'u-bob', 'enable');
    assert.equal(confirmView(page).passwordShown, false);
    await u.submit(page, 'userConfirmSubmit');
    assert.deepEqual(u.userRequests(page, 'PUT'), [{ path: '/api/users/u-bob/active', body: { isActive: true } }]);
});

test('Delete names the account and what is deleted with it', async () => {
    const page = await u.bootUsers();
    page.route('DELETE /api/users/u-bob', () => {
        page.server.users = [u.ME];
        return undefined;
    });

    await u.clickAction(page, 'u-bob', 'delete');
    assert.deepEqual(confirmView(page), {
        title: 'Delete Account',
        message: 'Delete bob (Bob Builder)? This cannot be undone. It deletes: the account, its sign-in sessions and its API key its reading progress and read status its reader and app preferences its e-reader devices, with their series subscriptions its email delivery history Comic files are not touched.',
        passwordShown: false,
        button: 'Delete',
        danger: true,
        primary: false
    });
    assert.equal(page.$$('#userConfirmMessage li').length, 5);

    await u.submit(page, 'userConfirmSubmit');
    assert.deepEqual(u.userRequests(page, 'DELETE'), [{ path: '/api/users/u-bob', body: undefined }]);
    assert.deepEqual(u.takeMessages(page), [{ type: 'success', text: 'Deleted the account bob.' }]);
    assert.deepEqual(u.cardIds(page), ['u-alice']);
    assert.equal(focusedId(page), 'usersPageTitle', 'the deleted card is gone, so the keyboard goes to the page title');
});

test('with Authelia, Delete says the account comes back at the next Authelia sign-in', async () => {
    const page = await u.bootUsers({ autheliaEnabled: true });
    await u.clickAction(page, 'u-bob', 'delete');
    assert.match(confirmView(page).message, /Comic files are not touched\. Authelia is enabled: if this person signs in through Authelia again, a new, empty account is created for them\. To keep them out, disable the account instead, or remove them in Authelia\.$/);
});

test('a refused Disable, Enable or Delete is explained in the dialog', async () => {
    const page = await u.bootUsers({ users: [u.ME, u.BOB, u.account({ id: 'u-carol', username: 'carol', isActive: false })] });
    page.route('PUT /api/users/u-bob/active', respond(409, { error: 'bob is the only active administrator.' }));
    page.route('DELETE /api/users/u-bob', respond(500, { error: 'Database is locked' }));
    page.route('PUT /api/users/u-carol/active', respond(403, { error: 'The current password is missing or wrong.' }));

    await u.clickAction(page, 'u-bob', 'disable');
    const loads = u.listLoads(page);
    await u.submit(page, 'userConfirmSubmit');
    assert.equal(page.$('#userConfirmError').textContent, 'bob is the only active administrator.');
    assert.ok(page.$('#userConfirmError').classList.contains('show'));
    assert.ok(u.isOpen(page, 'userConfirmModal'));
    assert.equal(u.listLoads(page), loads + 1, 'the list is reloaded behind the dialog');
    assert.equal(page.$('#userConfirmSubmit').disabled, false);

    page.click('#userConfirmCancel');
    await page.settle();
    await u.clickAction(page, 'u-bob', 'delete');
    assert.equal(page.$('#userConfirmError').classList.contains('show'), false, 'a new dialog starts without the old error');
    await u.submit(page, 'userConfirmSubmit');
    assert.equal(page.$('#userConfirmError').textContent, 'Database is locked');
    assert.equal(u.listLoads(page), loads + 1);

    press(page, 'Escape');
    await page.settle();
    await u.clickAction(page, 'u-carol', 'enable');
    u.fill(page, { userConfirmCurrentPassword: 'Wr0ngGuess' });
    await u.submit(page, 'userConfirmSubmit');
    assert.equal(page.$('#userConfirmError').textContent, 'The current password is missing or wrong.');
    assert.equal(page.$('#userConfirmCurrentPassword').value, '');
    assert.equal(focusedId(page), 'userConfirmCurrentPassword');
});

test('a confirmation is sent once, and only for a known account and action', async () => {
    const page = await u.bootUsers();
    page.run("openUserConfirmModal('u-nobody', 'delete'); openUserConfirmModal('u-bob', 'promote'); submitUserConfirm();");
    await page.settle();
    assert.equal(u.isOpen(page, 'userConfirmModal'), false);

    const pending = deferred();
    page.route('DELETE /api/users/u-bob', () => pending.promise);
    await u.clickAction(page, 'u-bob', 'delete');
    await u.submit(page, 'userConfirmSubmit');
    assert.equal(page.$('#userConfirmSubmit').disabled, true);
    page.run('submitUserConfirm()');
    await page.settle();
    pending.resolve(undefined);
    await page.settle();
    assert.equal(u.userRequests(page, 'DELETE').length, 1);
});

test('leaving the page, or losing the administrator role, closes the Users dialogs', async () => {
    const page = await u.bootUsers();
    await u.clickAction(page, 'u-bob', 'disable');
    assert.ok(u.isOpen(page, 'userConfirmModal'));

    page.window.location.hash = '#/home';
    await page.settle();
    assert.equal(u.isOpen(page, 'userConfirmModal'), false);
    assert.equal(page.evaluate('userDialogAccount'), null);
    assert.equal(page.evaluate('userConfirmAction'), null);
    // Nothing is left to submit.
    page.run('submitUserConfirm()');
    await page.settle();
    assert.deepEqual(u.userRequests(page, 'PUT'), []);

    page.window.location.hash = '#/users';
    await page.settle();
    await u.clickAction(page, 'u-bob', 'role');
    page.run('userCapabilities = { canModifyLibrary: true, canAdminister: false }; applyCapabilities();');
    await page.settle();
    assert.equal(u.isOpen(page, 'userRoleModal'), false);
    assert.equal(page.$('#usersAdminNotice').hidden, false);
    assert.equal(page.$('#usersList').hidden, true);
});

test('a change whose list reload fails leaves the keyboard where it was', async () => {
    const page = await u.bootUsers();
    page.route('PUT /api/users/u-bob/active', () => {
        page.route(`GET ${u.USERS}`, respond(500, { error: 'Database is locked' }));
        return u.BOB;
    });

    await u.clickAction(page, 'u-bob', 'disable');
    await u.submit(page, 'userConfirmSubmit');
    assert.equal(u.isOpen(page, 'userConfirmModal'), false);
    assert.match(u.text(page.$('#usersList')), /^Users could not be loaded: Database is locked/);
    assert.notEqual(focusedId(page), 'usersPageTitle');
});
