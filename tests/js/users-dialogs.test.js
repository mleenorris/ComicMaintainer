'use strict';

// The users page's Add User, Change Role and Reset Password dialogs
// (js/app/users.js): what they ask for, the checks made before sending, the
// requests, and what a refusal from the server looks like. Disable, Enable
// and Delete are in users-confirm.test.js.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { press, wentToLogin } = require('./support/core-helpers');
const u = require('./support/users-helpers');

const PASSWORD_RULE = 'The password needs at least 8 characters, with an upper-case letter, a lower-case letter and a digit.';
const ROLES = [
    { value: 'User', label: 'User', description: 'Browses and reads the library, and changes it: processing, renaming, metadata, deleting files and emailing comics.' },
    { value: 'ReadOnly', label: 'Read-only', description: 'Browses and reads the library and keeps their own reading progress. Cannot change files or metadata, or email comics.' },
    { value: 'Admin', label: 'Administrator', description: 'Everything a User can do, plus server settings, accounts, backups, logs and scheduled jobs.' }
];
const roles = checked => ROLES.map(role => ({ ...role, checked: role.value === checked }));
const focusedId = page => page.document.activeElement && page.document.activeElement.id;

const VALID_ACCOUNT = {
    addUserUsername: '  carol  ',
    addUserPassword: 'Corr3ctHorse',
    addUserConfirmPassword: 'Corr3ctHorse',
    addUserCurrentPassword: 'MyOwnPassw0rd'
};

async function openAddUser(page) {
    // A click focuses the button in a browser; jsdom's click() does not.
    page.$('#addUserButton').focus();
    page.click('#addUserButton');
    await page.settle();
}

test('Add User asks for the account, its role and the administrator\'s password', async () => {
    const page = await u.bootUsers();
    await openAddUser(page);

    assert.ok(u.isOpen(page, 'addUserModal'));
    assert.equal(page.$('#addUserModal').getAttribute('aria-hidden'), 'false');
    assert.equal(focusedId(page), 'addUserUsername', 'the keyboard starts on the first field');
    assert.deepEqual(u.roleOptions(page, 'addUserRoleOptions'), roles('User'));
    assert.equal(page.$('#addUserCurrentPasswordGroup').hidden, false);
    for (const id of ['addUserUsername', 'addUserFullName', 'addUserEmail', 'addUserPassword', 'addUserConfirmPassword', 'addUserCurrentPassword']) {
        assert.ok(page.$(`label[for="${id}"]`), `#${id} is labelled`);
    }
    assert.equal(u.dialogError(page, 'addUserError'), null);

    // Escape closes it and gives the keyboard back to the button.
    press(page, 'Escape');
    await page.settle();
    assert.equal(u.isOpen(page, 'addUserModal'), false);
    assert.equal(focusedId(page), 'addUserButton');
});

test('Add User checks the account before sending it', async () => {
    const page = await u.bootUsers();
    const cases = [
        [{ addUserUsername: ' ab ' }, 'addUserUsername', 'Enter a username of at least 3 characters.'],
        [{ addUserEmail: 'carol.example.com' }, 'addUserEmail', 'Enter a valid email address, or leave the email empty.'],
        [{ addUserEmail: '@example.com' }, 'addUserEmail', 'Enter a valid email address, or leave the email empty.'],
        [{ addUserPassword: 'Sh0rt', addUserConfirmPassword: 'Sh0rt' }, 'addUserPassword', PASSWORD_RULE],
        [{ addUserPassword: 'n0uppercase' }, 'addUserPassword', PASSWORD_RULE],
        [{ addUserPassword: 'N0LOWERCASE' }, 'addUserPassword', PASSWORD_RULE],
        [{ addUserPassword: 'NoDigitsHere' }, 'addUserPassword', PASSWORD_RULE],
        [{ addUserConfirmPassword: 'Corr3ctHorse!' }, 'addUserConfirmPassword', 'The passwords do not match.'],
        [{ addUserCurrentPassword: '' }, 'addUserCurrentPassword', 'Enter your own password to confirm.']
    ];
    for (const [values, field, error] of cases) {
        await openAddUser(page);
        u.fill(page, { ...VALID_ACCOUNT, ...values });
        await u.submit(page, 'addUserSubmit');
        assert.equal(u.dialogError(page, 'addUserError'), error, field);
        assert.equal(page.$(`#${field}`).getAttribute('aria-invalid'), 'true', field);
        assert.equal(focusedId(page), field);
        assert.equal(page.$$('#addUserModal [aria-invalid]').length, 1, 'only the refused field is marked');
        assert.ok(u.isOpen(page, 'addUserModal'));
        page.click('#addUserModal .modal-footer .btn:not([type="submit"])');
        await page.settle();
    }
    assert.deepEqual(u.userRequests(page, 'POST'), []);

    // Opening the dialog again starts from an empty, unmarked form.
    await openAddUser(page);
    assert.equal(page.$('#addUserUsername').value, '');
    assert.equal(page.$$('#addUserModal [aria-invalid]').length, 0);
    assert.equal(u.dialogError(page, 'addUserError'), null);
});

test('Add User creates the account, closes and shows it in the list', async () => {
    const page = await u.bootUsers();
    const carol = u.account({ id: 'u-carol', username: 'carol', fullName: null, email: null, role: 'ReadOnly', lastLoginAt: null });
    page.route(`POST ${u.USERS}`, call => {
        page.server.users = [u.ME, u.BOB, carol];
        return respond(201, { ...carol, username: call.body.username });
    });

    await openAddUser(page);
    u.fill(page, VALID_ACCOUNT);
    page.click('#addUserRole-ReadOnly');
    await u.submit(page, 'addUserSubmit');

    assert.deepEqual(u.userRequests(page, 'POST'), [{
        path: u.USERS,
        body: { username: 'carol', password: 'Corr3ctHorse', email: null, fullName: null, role: 'ReadOnly', currentPassword: 'MyOwnPassw0rd' }
    }]);
    assert.equal(u.isOpen(page, 'addUserModal'), false);
    assert.deepEqual(u.takeMessages(page), [{ type: 'success', text: 'Added the account carol.' }]);
    assert.deepEqual(u.cardIds(page), ['u-alice', 'u-bob', 'u-carol']);
    assert.equal(focusedId(page), 'user-account-u-carol-name', 'the keyboard moves to the new account');

    // A full name and email are sent trimmed.
    page.route(`POST ${u.USERS}`, respond(201, {}));
    await openAddUser(page);
    u.fill(page, { ...VALID_ACCOUNT, addUserFullName: '  Carol Danvers ', addUserEmail: ' carol@example.com ' });
    await u.submit(page, 'addUserSubmit');
    const [, second] = u.userRequests(page, 'POST');
    assert.deepEqual(second.body, { username: 'carol', password: 'Corr3ctHorse', email: 'carol@example.com', fullName: 'Carol Danvers', role: 'User', currentPassword: 'MyOwnPassw0rd' });
    // Without an id to go to, the keyboard lands on the page title.
    assert.equal(focusedId(page), 'usersPageTitle');
});

test('a refusal from the server is shown in the Add User dialog', async () => {
    const page = await u.bootUsers();
    const answers = [
        [respond(400, { error: "Username 'car ol' is invalid, can only contain letters or digits." }), "Username 'car ol' is invalid, can only contain letters or digits.", 1],
        [respond(409, { error: 'An account named carol already exists.' }), 'An account named carol already exists.', 2],
        [respond(500, { error: 'Database is locked' }, { 'X-Correlation-Id': 'ref-9' }), 'Database is locked (ref: ref-9)', 1]
    ];
    for (const [answer, error, loads] of answers) {
        page.route(`POST ${u.USERS}`, answer);
        await openAddUser(page);
        u.fill(page, VALID_ACCOUNT);
        const before = u.listLoads(page);
        await u.submit(page, 'addUserSubmit');
        assert.equal(u.dialogError(page, 'addUserError'), error);
        assert.ok(u.isOpen(page, 'addUserModal'));
        assert.equal(page.$('#addUserCurrentPassword').value, 'MyOwnPassw0rd', 'the password is kept');
        assert.equal(page.$('#addUserSubmit').disabled, false);
        // A conflict means the list is out of date, so it is reloaded behind the dialog.
        assert.equal(u.listLoads(page) - before, loads - 1);
    }
});

test('a wrong password of the administrator\'s own is cleared for retyping', async () => {
    const page = await u.bootUsers();
    page.route(`POST ${u.USERS}`, respond(403, { error: 'The current password is missing or wrong.' }));
    await openAddUser(page);
    u.fill(page, VALID_ACCOUNT);
    await u.submit(page, 'addUserSubmit');

    assert.equal(u.dialogError(page, 'addUserError'), 'The current password is missing or wrong.');
    assert.equal(page.$('#addUserCurrentPassword').value, '');
    assert.equal(page.$('#addUserCurrentPassword').getAttribute('aria-invalid'), 'true');
    assert.equal(focusedId(page), 'addUserCurrentPassword');
    assert.equal(page.$('#addUserPassword').value, 'Corr3ctHorse', 'the new account\'s fields are kept');

    // A bare 403 (the caller is no longer an administrator) keeps the password.
    page.route(`POST ${u.USERS}`, respond(403));
    u.fill(page, { addUserCurrentPassword: 'MyOwnPassw0rd' });
    await u.submit(page, 'addUserSubmit');
    assert.equal(u.dialogError(page, 'addUserError'), 'Only administrators can manage users.');
    assert.equal(page.$('#addUserCurrentPassword').value, 'MyOwnPassw0rd');
});

test('Add User sends once while a request is pending, and an expired session goes to the login page', async () => {
    const page = await u.bootUsers();
    const pending = deferred();
    page.route(`POST ${u.USERS}`, () => pending.promise);
    await openAddUser(page);
    u.fill(page, VALID_ACCOUNT);
    await u.submit(page, 'addUserSubmit');
    assert.equal(page.$('#addUserSubmit').disabled, true);
    page.run('submitAddUser()');
    await page.settle();
    assert.equal(u.userRequests(page, 'POST').length, 1);

    pending.resolve(401);
    await page.settle();
    assert.ok(wentToLogin(page));
    assert.equal(u.dialogError(page, 'addUserError'), null);
    assert.equal(page.$('#addUserSubmit').disabled, false);
});

test('an administrator signed in through Authelia is not asked for a password', async () => {
    const page = await u.bootUsers({ user: { authMethod: 'authelia' } });
    page.route(`POST ${u.USERS}`, respond(201, { id: 'u-carol' }));
    await openAddUser(page);
    assert.equal(page.$('#addUserCurrentPasswordGroup').hidden, true);

    u.fill(page, { ...VALID_ACCOUNT, addUserCurrentPassword: '' });
    await u.submit(page, 'addUserSubmit');
    const [request] = u.userRequests(page, 'POST');
    assert.equal('currentPassword' in request.body, false);
    assert.equal(u.isOpen(page, 'addUserModal'), false);

    // A 403 then has no password field to clear.
    page.route(`POST ${u.USERS}`, respond(403, { error: 'Forbidden by the proxy.' }));
    await openAddUser(page);
    u.fill(page, VALID_ACCOUNT);
    await u.submit(page, 'addUserSubmit');
    assert.equal(u.dialogError(page, 'addUserError'), 'Forbidden by the proxy.');
    assert.equal(page.$('#addUserCurrentPassword').hasAttribute('aria-invalid'), false);
});

test('Change Role starts on the current role and sends the new one with the password', async () => {
    const page = await u.bootUsers();
    page.route('PUT /api/users/u-bob/role', call => {
        page.server.users = [u.ME, { ...u.BOB, role: call.body.role }];
        return { ...u.BOB, role: call.body.role };
    });

    await u.clickAction(page, 'u-bob', 'role');
    assert.ok(u.isOpen(page, 'userRoleModal'));
    assert.equal(u.text(page.$('#userRoleIntro')), 'Choose what bob (Bob Builder) can do. Changing the role signs the account out everywhere, so it signs in again with the new role.');
    assert.deepEqual(u.roleOptions(page, 'userRoleOptions'), roles('User'));
    assert.equal(focusedId(page), 'userRoleChoice-User');

    // Keeping the role is not a change: nothing is sent and no password is needed.
    await u.submit(page, 'userRoleSubmit');
    assert.equal(u.isOpen(page, 'userRoleModal'), false);
    assert.deepEqual(u.takeMessages(page), [{ type: 'info', text: 'bob already has the role User.' }]);
    assert.deepEqual(u.userRequests(page, 'PUT'), []);

    await u.clickAction(page, 'u-bob', 'role');
    page.click('#userRoleChoice-Admin');
    await u.submit(page, 'userRoleSubmit');
    assert.equal(u.dialogError(page, 'userRoleError'), 'Enter your own password to confirm.');
    assert.equal(focusedId(page), 'userRoleCurrentPassword');
    assert.deepEqual(u.userRequests(page, 'PUT'), []);

    u.fill(page, { userRoleCurrentPassword: 'MyOwnPassw0rd' });
    await u.submit(page, 'userRoleSubmit');
    assert.deepEqual(u.userRequests(page, 'PUT'), [{ path: '/api/users/u-bob/role', body: { role: 'Admin', currentPassword: 'MyOwnPassw0rd' } }]);
    assert.equal(u.isOpen(page, 'userRoleModal'), false);
    assert.deepEqual(u.takeMessages(page), [{ type: 'success', text: 'bob now has the role Administrator. Their sessions have ended, so they sign in again.' }]);
    assert.equal(u.cardView(page, 'u-bob').fields.Role, 'Administrator');
    assert.equal(focusedId(page), 'user-account-u-bob-name');
});

test('Change Role of an account without a role offers User, and shows refusals', async () => {
    const nobody = u.account({ id: 'u-x', username: 'x-files', fullName: null, role: null });
    const page = await u.bootUsers({ users: [u.ME, nobody], user: { authMethod: 'authelia' } });
    page.route('PUT /api/users/u-x/role', respond(409, { error: 'Authelia is enabled, so roles come from Authelia groups.' }));

    await u.clickAction(page, 'u-x', 'role');
    assert.equal(u.text(page.$('#userRoleIntro')).startsWith('Choose what x-files can do.'), true);
    assert.equal(page.$('#userRoleCurrentPasswordGroup').hidden, true);

    // Nothing checked at all (e.g. markup changed under the dialog) reads as User.
    page.$$('#userRoleOptions input').forEach(input => { input.checked = false; });
    await u.submit(page, 'userRoleSubmit');
    assert.deepEqual(u.userRequests(page, 'PUT'), [{ path: '/api/users/u-x/role', body: { role: 'User' } }]);
    assert.equal(u.dialogError(page, 'userRoleError'), 'Authelia is enabled, so roles come from Authelia groups.');
    assert.ok(u.isOpen(page, 'userRoleModal'));
});

test('the role choice is not focused once the dialog has closed', async () => {
    const page = await u.bootUsers();
    await u.clickAction(page, 'u-bob', 'role');
    page.run("closeUserRoleModal(); openUserRoleModal('u-bob'); closeUserRoleModal();");
    await page.settle();
    assert.notEqual(focusedId(page), 'userRoleChoice-User');
});

test('Reset Password checks the new password and sends it with the administrator\'s', async () => {
    const locked = u.account({ lockedOutUntil: new Date(Date.now() + 60000).toISOString(), hasApiKey: true });
    const page = await u.bootUsers({ users: [u.ME, locked] });
    page.route('POST /api/users/u-bob/password', () => {
        page.server.users = [u.ME, u.BOB];
        return u.BOB;
    });

    await u.clickAction(page, 'u-bob', 'password');
    assert.ok(u.isOpen(page, 'userPasswordModal'));
    assert.equal(u.text(page.$('#userPasswordIntro')), 'Set a new password for bob (Bob Builder). This signs the account out everywhere, revokes its API key and lifts a lockout. Tell them the new password; they can change it with Change Password.');
    assert.equal(focusedId(page), 'userPasswordNew');

    const cases = [
        [{ userPasswordNew: 'weak', userPasswordConfirm: 'weak' }, 'userPasswordNew', PASSWORD_RULE],
        [{ userPasswordNew: 'N3wPassword', userPasswordConfirm: 'N3wPasswort' }, 'userPasswordConfirm', 'The passwords do not match.'],
        [{ userPasswordNew: 'N3wPassword', userPasswordConfirm: 'N3wPassword', userPasswordCurrentPassword: '' }, 'userPasswordCurrentPassword', 'Enter your own password to confirm.']
    ];
    for (const [values, field, error] of cases) {
        u.fill(page, values);
        await u.submit(page, 'userPasswordSubmit');
        assert.equal(u.dialogError(page, 'userPasswordError'), error);
        assert.equal(focusedId(page), field);
    }
    assert.deepEqual(u.userRequests(page, 'POST'), []);

    page.route('POST /api/users/u-bob/password', respond(403, { error: 'Too many wrong passwords. Try again later.' }));
    u.fill(page, { userPasswordCurrentPassword: 'Wr0ngGuess' });
    await u.submit(page, 'userPasswordSubmit');
    assert.equal(u.dialogError(page, 'userPasswordError'), 'Too many wrong passwords. Try again later.');
    assert.equal(page.$('#userPasswordCurrentPassword').value, '');

    page.route('POST /api/users/u-bob/password', () => {
        page.server.users = [u.ME, u.BOB];
        return u.BOB;
    });
    u.fill(page, { userPasswordCurrentPassword: 'MyOwnPassw0rd' });
    await u.submit(page, 'userPasswordSubmit');
    const requests = u.userRequests(page, 'POST');
    assert.deepEqual(requests.at(-1), { path: '/api/users/u-bob/password', body: { newPassword: 'N3wPassword', currentPassword: 'MyOwnPassw0rd' } });
    assert.equal(u.isOpen(page, 'userPasswordModal'), false);
    assert.deepEqual(u.takeMessages(page), [{ type: 'success', text: 'Reset the password of bob. The account is signed out everywhere and its API key is revoked.' }]);
    assert.equal(u.cardView(page, 'u-bob').status, 'Active');
    assert.equal(focusedId(page), 'user-account-u-bob-name');
});

test('an account that is gone is reported and the list reloaded', async () => {
    const page = await u.bootUsers({ user: { authMethod: 'authelia' } });
    page.route('POST /api/users/u-bob/password', () => {
        page.server.users = [u.ME];
        return respond(404, { error: 'There is no such account.' });
    });

    await u.clickAction(page, 'u-bob', 'password');
    assert.equal(page.$('#userPasswordCurrentPasswordGroup').hidden, true);
    u.fill(page, { userPasswordNew: 'N3wPassword', userPasswordConfirm: 'N3wPassword' });
    await u.submit(page, 'userPasswordSubmit');
    assert.deepEqual(u.userRequests(page, 'POST'), [{ path: '/api/users/u-bob/password', body: { newPassword: 'N3wPassword' } }]);
    assert.equal(u.dialogError(page, 'userPasswordError'), 'There is no such account.');
    assert.deepEqual(u.cardIds(page), ['u-alice']);
});

test('dialog submissions do nothing without an account to act on', async () => {
    const page = await u.bootUsers();
    page.run("openUserRoleModal('u-nobody'); openUserPasswordModal('u-nobody'); submitUserRole(); submitUserPassword();");
    await page.settle();
    assert.equal(u.isOpen(page, 'userRoleModal'), false);
    assert.equal(u.isOpen(page, 'userPasswordModal'), false);

    // A second click while a change is being sent is ignored.
    const pending = deferred();
    page.route('PUT /api/users/u-bob/role', () => pending.promise);
    await u.clickAction(page, 'u-bob', 'role');
    page.click('#userRoleChoice-ReadOnly');
    u.fill(page, { userRoleCurrentPassword: 'MyOwnPassw0rd' });
    await u.submit(page, 'userRoleSubmit');
    page.run('submitUserRole()');
    await page.settle();
    pending.resolve(u.BOB);
    await page.settle();
    assert.equal(u.userRequests(page, 'PUT').length, 1);

    const pendingPassword = deferred();
    page.route('POST /api/users/u-bob/password', () => pendingPassword.promise);
    await u.clickAction(page, 'u-bob', 'password');
    u.fill(page, { userPasswordNew: 'N3wPassword', userPasswordConfirm: 'N3wPassword', userPasswordCurrentPassword: 'MyOwnPassw0rd' });
    await u.submit(page, 'userPasswordSubmit');
    page.run('submitUserPassword()');
    await page.settle();
    pendingPassword.resolve(u.BOB);
    await page.settle();
    assert.equal(u.userRequests(page, 'POST').length, 1);
});
