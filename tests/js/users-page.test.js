'use strict';

// The users page (#/users, js/app/users.js) in the booted SPA: opening it,
// the account cards and the actions the server would refuse, loading and its
// failures, and the administrator-only notice. The dialogs are in
// users-dialogs.test.js and users-confirm.test.js.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { consoleLines, press, requests, wentToLogin } = require('./support/core-helpers');
const u = require('./support/users-helpers');

const shown = date => new Date(date).toLocaleString();
const listText = page => u.text(page.$('#usersList'));
const keys = async (page, ...sequence) => {
    for (const key of sequence) press(page, key, {}, page.document.body);
    await page.settle();
};

test('the sidebar, the account menu and "g u" open the users page', async () => {
    const page = await u.bootUsers({ hash: '#/library' });
    assert.equal(page.$('#usersView').hidden, true);
    assert.equal(u.listLoads(page), 0, 'the accounts are only loaded once the page is opened');

    page.click('#sidebarUsersBtn');
    await page.settle();
    assert.equal(page.window.location.hash, '#/users');
    assert.equal(page.$('#usersView').hidden, false);
    assert.equal(page.document.title, 'Users · Comic Maintainer');
    assert.equal(page.$('#topbarTitle').textContent, 'Users');
    assert.equal(page.$('#sidebarUsersBtn').getAttribute('aria-current'), 'page');
    const [load] = requests(page, 'GET', u.USERS);
    assert.equal(load.init.credentials, 'include');
    assert.match(load.headers.authorization, /^Bearer /);
    assert.deepEqual(u.cardIds(page), ['u-alice', 'u-bob']);
    assert.equal(u.text(page.$('.users-count')), '2 accounts');
    assert.equal(page.$('#usersList').getAttribute('aria-busy'), 'false');
    assert.equal(page.$('#usersAdminNotice').hidden, true);
    assert.equal(page.$('#usersToolbar').hidden, false);
    assert.equal(page.$('#addUserButton').disabled, false);
    assert.equal(page.$('#addUserButton').hasAttribute('aria-describedby'), false);
    assert.equal(page.$('#usersAutheliaNotice').hidden, true);

    page.window.location.hash = '#/library';
    await page.settle();
    assert.equal(page.$('#usersView').hidden, true);
    assert.equal(page.$('#sidebarUsersBtn').hasAttribute('aria-current'), false);
    page.click('#usersMenuItem');
    await page.settle();
    assert.equal(page.window.location.hash, '#/users');
    assert.equal(u.listLoads(page), 2);

    page.window.location.hash = '#/home';
    await page.settle();
    await keys(page, 'g', 'u');
    assert.equal(page.window.location.hash, '#/users');
    assert.equal(u.listLoads(page), 3);
    assert.ok(!page.$('#usersShortcutItem').hidden, 'the shortcut is listed in the help');
});

test('each card shows the account, its role, status and dates', async () => {
    const LOCK = new Date(Date.now() + 10 * 60 * 1000).toISOString();
    const users = [
        u.ME,
        u.account({ id: 'u-carol', username: 'carol', fullName: null, email: null, role: 'ReadOnly', lastLoginAt: null, hasApiKey: true }),
        u.account({ id: 'u-dave', username: 'dave', role: 'Admin', isActive: false, lockedOutUntil: LOCK }),
        u.account({ id: 'u-erin', username: 'erin', role: null, lockedOutUntil: LOCK }),
        u.account({ id: 'u-frank', username: 'frank', role: 'Moderator', lockedOutUntil: '2020-01-01T00:00:00Z', createdAt: 'not a date' })
    ];
    const page = await u.bootUsers({ users });

    assert.deepEqual(u.cardView(page, 'u-alice'), {
        name: 'alice',
        you: true,
        fullName: 'Alice Admin',
        status: 'Active',
        statusClass: 'user-account-status--active',
        fields: { Email: 'alice@example.com', Role: 'Administrator', 'Last sign-in': shown(u.ME.lastLoginAt), Created: shown(u.ME.createdAt), 'API key': 'None' },
        actions: {
            role: { label: 'Change role', disabled: true, describedBy: 'user-account-u-alice-own' },
            password: { label: 'Reset password', disabled: true, describedBy: 'user-account-u-alice-own' },
            disable: { label: 'Disable', disabled: true, describedBy: 'user-account-u-alice-own' },
            delete: { label: 'Delete', disabled: true, describedBy: 'user-account-u-alice-own' }
        },
        notes: [{
            id: 'user-account-u-alice-own',
            text: 'This is your account. You cannot change its role, disable it or delete it here. To change your password, use Change Password in the account menu.'
        }]
    });

    const carol = u.cardView(page, 'u-carol');
    assert.equal(carol.you, false);
    assert.equal(carol.fullName, null);
    assert.deepEqual(carol.fields, { Email: '—', Role: 'Read-only', 'Last sign-in': 'Never', Created: shown(u.BOB.createdAt), 'API key': 'Yes' });
    assert.deepEqual(Object.values(carol.actions).map(action => action.disabled), [false, false, false, false]);
    assert.deepEqual(Object.values(carol.actions).map(action => action.describedBy), [null, null, null, null]);
    assert.deepEqual(carol.notes, []);

    // A disabled account says so even while locked out, and offers Enable.
    const dave = u.cardView(page, 'u-dave');
    assert.equal(dave.status, 'Disabled');
    assert.equal(dave.statusClass, 'user-account-status--disabled');
    assert.deepEqual(Object.keys(dave.actions), ['role', 'password', 'enable', 'delete']);
    assert.deepEqual(dave.actions.enable, { label: 'Enable', disabled: false, describedBy: null });
    assert.deepEqual(dave.notes, [], 'a disabled administrator is not the last active one');

    const erin = u.cardView(page, 'u-erin');
    assert.equal(erin.status, `Locked until ${shown(LOCK)}`);
    assert.equal(erin.statusClass, 'user-account-status--locked');
    assert.equal(erin.fields.Role, 'No role');

    // A lockout that has ended is not shown; a role the page does not know is named as it is.
    const frank = u.cardView(page, 'u-frank');
    assert.equal(frank.status, 'Active');
    assert.equal(frank.fields.Role, 'Moderator');
    assert.equal(frank.fields.Created, '—');

    // The status badge names itself to screen readers.
    assert.equal(u.card(page, 'u-alice').querySelector('[data-user-part="status"] .visually-hidden').textContent, 'Status: ');
    assert.equal(page.$('#usersList ul').getAttribute('aria-label'), 'Accounts');
});

test('the only active administrator cannot be demoted, disabled or deleted', async () => {
    // With Authelia and no admin groups every non-ReadOnly user administers,
    // so the caller need not be in the Admin role themselves.
    const me = { ...u.ME, role: 'User' };
    const page = await u.bootUsers({ users: [me, u.account({ role: 'Admin' }), u.account({ id: 'u-old', username: 'old', role: 'Admin', isActive: false })] });

    const bob = u.cardView(page, 'u-bob');
    const note = 'user-account-u-bob-last-admin';
    assert.deepEqual(bob.actions, {
        role: { label: 'Change role', disabled: true, describedBy: note },
        password: { label: 'Reset password', disabled: false, describedBy: null },
        disable: { label: 'Disable', disabled: true, describedBy: note },
        delete: { label: 'Delete', disabled: true, describedBy: note }
    });
    assert.deepEqual(bob.notes, [{ id: note, text: 'This is the only active administrator. Make another account an administrator before you change its role, disable it or delete it.' }]);
    assert.deepEqual(u.cardView(page, 'u-old').notes, []);

    // With a second active administrator either may be changed.
    page.server.users = [u.ME, u.account({ role: 'Admin' })];
    page.run('loadUsers()');
    await page.settle();
    assert.deepEqual(Object.values(u.cardView(page, 'u-bob').actions).map(action => action.disabled), [false, false, false, false]);
});

test('with Authelia, accounts cannot be added and roles and passwords cannot be changed', async () => {
    const page = await u.bootUsers({ autheliaEnabled: true });

    const notice = page.$('#usersAutheliaNotice');
    assert.equal(notice.hidden, false);
    assert.match(u.text(notice), /^Authelia is enabled\. Accounts are created when someone first signs in through Authelia/);
    assert.equal(page.$('#addUserButton').disabled, true);
    assert.equal(page.$('#addUserButton').getAttribute('aria-describedby'), 'usersAutheliaNotice');
    assert.deepEqual(u.cardView(page, 'u-bob').actions, {
        role: { label: 'Change role', disabled: true, describedBy: 'usersAutheliaNotice' },
        password: { label: 'Reset password', disabled: true, describedBy: 'usersAutheliaNotice' },
        disable: { label: 'Disable', disabled: false, describedBy: null },
        delete: { label: 'Delete', disabled: false, describedBy: null }
    });
    // The caller's own account gives both reasons.
    assert.equal(u.cardView(page, 'u-alice').actions.role.describedBy, 'user-account-u-alice-own usersAutheliaNotice');

    // Authelia turned off again: the next load lifts the refusals.
    page.server.autheliaEnabled = false;
    page.run('loadUsers()');
    await page.settle();
    assert.equal(notice.hidden, true);
    assert.equal(page.$('#addUserButton').disabled, false);
    assert.equal(page.$('#addUserButton').hasAttribute('aria-describedby'), false);
    assert.equal(u.cardView(page, 'u-bob').actions.role.disabled, false);
});

test('an empty or unexpected answer lists no accounts', async () => {
    for (const answer of [{ users: [] }, {}, null, [], { users: 'none' }]) {
        const page = await u.bootUsers({ routes: { [`GET ${u.USERS}`]: answer === null ? respond(200, 'null', { 'Content-Type': 'application/json' }) : answer } });
        assert.equal(listText(page), 'There are no accounts.', JSON.stringify(answer));
        assert.equal(page.$('#usersToolbar').hidden, false);
    }
});

test('one account is counted in the singular', async () => {
    const page = await u.bootUsers({ users: [u.ME] });
    assert.equal(u.text(page.$('.users-count')), '1 account');
});

test('a load that fails says why and offers Retry', async () => {
    const page = await u.bootUsers({
        routes: { [`GET ${u.USERS}`]: respond(500, { error: 'Database is locked' }, { 'X-Correlation-Id': 'ref-123' }) }
    });
    assert.equal(listText(page), 'Users could not be loaded: Database is locked (ref: ref-123) Retry');
    assert.ok(consoleLines(page, 'error').includes('Failed to load users: Database is locked'));
    assert.equal(page.$('#usersList').getAttribute('aria-busy'), 'false');

    page.route(`GET ${u.USERS}`, () => ({ autheliaEnabled: false, users: [u.ME] }));
    page.click(page.$('#usersList .empty-state button'));
    await page.settle();
    assert.deepEqual(u.cardIds(page), ['u-alice']);
});

test('reloading keeps the accounts on screen until the answer arrives', async () => {
    const page = await u.bootUsers();
    const pending = deferred();
    page.route(`GET ${u.USERS}`, () => pending.promise);

    page.run('loadUsers()');
    await page.settle();
    assert.deepEqual(u.cardIds(page), ['u-alice', 'u-bob']);
    assert.equal(page.$('#usersList .spinner'), null);
    assert.equal(page.$('#usersList').getAttribute('aria-busy'), 'true');

    pending.resolve({ autheliaEnabled: false, users: [u.ME] });
    await page.settle();
    assert.deepEqual(u.cardIds(page), ['u-alice']);
    assert.equal(page.$('#usersList').getAttribute('aria-busy'), 'false');
});

test('only the latest load is shown', async () => {
    const page = await u.bootUsers();
    const first = deferred();
    const second = deferred();
    const answers = [first, second];
    page.route(`GET ${u.USERS}`, () => answers.shift().promise);

    page.run('loadUsers(); loadUsers()');
    await page.settle();
    second.resolve({ autheliaEnabled: false, users: [u.ME] });
    await page.settle();
    first.resolve({ autheliaEnabled: true, users: [u.ME, u.BOB] });
    await page.settle();
    assert.deepEqual(u.cardIds(page), ['u-alice']);
    assert.equal(page.$('#usersAutheliaNotice').hidden, true);

    // A failure of a superseded load is not shown either.
    const late = deferred();
    page.route(`GET ${u.USERS}`, () => late.promise);
    page.run('loadUsers()');
    await page.settle();
    page.route(`GET ${u.USERS}`, () => ({ autheliaEnabled: false, users: [u.ME, u.BOB] }));
    page.run('loadUsers()');
    await page.settle();
    late.resolve(respond(500, { error: 'Too late' }));
    await page.settle();
    assert.deepEqual(u.cardIds(page), ['u-alice', 'u-bob']);
    assert.equal(page.$('#usersList').getAttribute('aria-busy'), 'false');
});

test('a 403 replaces the accounts with the administrator notice', async () => {
    const page = await u.bootUsers();
    page.route(`GET ${u.USERS}`, respond(403));
    page.run('loadUsers()');
    await page.settle();

    assert.equal(page.$('#usersAdminNotice').hidden, false);
    assert.equal(page.$('#usersList').hidden, true);
    assert.equal(page.$('#usersList').children.length, 0);
    assert.equal(page.$('#usersList').getAttribute('aria-busy'), 'false');
    assert.equal(page.$('#usersToolbar').hidden, true);
    assert.deepEqual(u.takeMessages(page), []);

    // Given the role back, the user opens the page again.
    page.route(`GET ${u.USERS}`, () => ({ autheliaEnabled: false, users: [u.ME] }));
    page.click('#sidebarUsersBtn');
    await page.settle();
    assert.equal(page.$('#usersAdminNotice').hidden, true);
    assert.equal(page.$('#usersList').hidden, false);
    assert.deepEqual(u.cardIds(page), ['u-alice']);
});

test('an expired session sends the user to the login page', async () => {
    const page = await u.bootUsers({ routes: { [`GET ${u.USERS}`]: 401 } });
    assert.ok(wentToLogin(page));
    assert.doesNotMatch(listText(page), /could not be loaded/);
});

test('non-administrators get a notice instead of the accounts and no way in', async () => {
    const page = await u.bootUsers({ user: { canAdminister: false }, routes: { [`GET ${u.USERS}`]: respond(403) } });
    assert.equal(page.$('#usersAdminNotice').hidden, false);
    assert.equal(page.$('#usersList').hidden, true);
    for (const id of ['sidebarUsersBtn', 'usersMenuItem', 'usersShortcutItem']) {
        assert.equal(page.$(`#${id}`).hidden, true, `#${id} is hidden`);
    }

    // Following an old link to the page does not even ask for the accounts.
    const loads = u.listLoads(page);
    page.window.location.hash = '#/home';
    await page.settle();
    await keys(page, 'g', 'u');
    assert.equal(page.window.location.hash, '#/home', '"g u" is only a shortcut for administrators');
    page.window.location.hash = '#/users';
    await page.settle();
    assert.equal(page.$('#usersAdminNotice').hidden, false);
    assert.equal(u.listLoads(page), loads);
});

test('accounts that arrive after the user turned out not to be an administrator are not shown', async () => {
    for (const answer of [{ autheliaEnabled: false, users: [u.ME] }, respond(500, { error: 'Database is locked' })]) {
        const pending = deferred();
        const page = await u.bootUsers({ user: { canAdminister: false }, routes: { [`GET ${u.USERS}`]: () => pending.promise } });
        // The page asks before the user's capabilities are known.
        assert.equal(u.listLoads(page), 1);
        assert.equal(page.$('#usersAdminNotice').hidden, false);

        pending.resolve(answer);
        await page.settle();
        assert.equal(page.$('#usersList').hidden, true);
        assert.equal(page.$('#usersList').children.length, 0);
        assert.deepEqual(consoleLines(page, 'error').filter(line => line.startsWith('Failed to load users')), []);
    }
});

test('the page tolerates missing markup', async () => {
    const page = await u.bootUsers();
    for (const selector of ['#usersToolbar', '#usersAutheliaNotice', '#addUserButton', '#usersAdminNotice']) {
        page.$(selector)?.remove();
    }
    page.run('loadUsers(); showUsersAdminOnly();');
    await page.settle();
    page.$('#usersList').remove();
    assert.equal(await page.run('loadUsers()'), false);
    page.run('renderUsersList(); showUsersAdminOnly(); renderUsersToolbar();');
    for (const id of ['addUserModal', 'userRoleModal', 'userPasswordModal', 'userConfirmModal']) page.$(`#${id}`).remove();
    page.run('closeUserDialogs()');
    assert.deepEqual(page.errors, []);
});
