        // ComicMaintainer SPA — users page (#/users): the accounts that can sign in, and adding, changing, disabling and deleting them.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // The roles an account can have, in the order the dialogs offer them,
        // with what each one lets the account do. They follow the server's
        // policies: CanModifyLibrary is every role but ReadOnly, CanAdminister
        // is Admin.
        const USER_ROLES = [
            { value: 'User', label: 'User', description: 'Browses and reads the library, and changes it: processing, renaming, metadata, deleting files and emailing comics.' },
            { value: 'ReadOnly', label: 'Read-only', description: 'Browses and reads the library and keeps their own reading progress. Cannot change files or metadata, or email comics.' },
            { value: 'Admin', label: 'Administrator', description: 'Everything a User can do, plus server settings, accounts, backups, logs and scheduled jobs.' }
        ];
        const DEFAULT_USER_ROLE = 'User';

        // The server's password rules (Identity's options in
        // AuthServiceCollectionExtensions): 8 characters with an ASCII upper-
        // case letter, lower-case letter and digit. It may still refuse a
        // password for another reason, such as one published as an example.
        const USER_PASSWORD_RULE = 'The password needs at least 8 characters, with an upper-case letter, a lower-case letter and a digit.';
        // What the server's email check (EmailAddressAttribute) accepts: one
        // "@", neither first nor last.
        const USER_EMAIL_PATTERN = /^[^@]+@[^@]+$/;

        // Disable, enable and delete share one confirmation dialog. Enabling
        // gives lasting access back, so it asks for the administrator's own
        // password; the other two only take access away.
        const USER_CONFIRM_ACTIONS = {
            disable: { title: 'Disable Account', button: 'Disable', danger: true, needsPassword: false },
            enable: { title: 'Enable Account', button: 'Enable', danger: false, needsPassword: true },
            delete: { title: 'Delete Account', button: 'Delete', danger: true, needsPassword: false }
        };

        const USER_DIALOG_IDS = ['addUserModal', 'userRoleModal', 'userPasswordModal', 'userConfirmModal'];

        let userAccounts = [];
        let usersAutheliaEnabled = false;
        let usersLoadRequest = 0;
        // The account the open Change Role, Reset Password or confirmation
        // dialog acts on, and what the confirmation dialog does.
        let userDialogAccount = null;
        let userConfirmAction = null;

        function openUsers() {
            navigate('#/users');
        }

        /** Calls a /api/users endpoint; see apiFetch() for what it returns and throws. */
        function fetchUsersApi(path, options = {}) {
            return apiFetch(path, { forbiddenMessage: 'Only administrators can manage users.', ...options });
        }

        function userRoleLabel(role) {
            const known = USER_ROLES.find(item => item.value === role);
            return known ? known.label : (role || 'No role');
        }

        function formatUserDate(value, fallback) {
            const date = parseScheduledJobDate(value);
            return date ? date.toLocaleString() : fallback;
        }

        function userAccountStatus(user) {
            if (!user.isActive) return { key: 'disabled', label: 'Disabled' };
            const lockedUntil = parseScheduledJobDate(user.lockedOutUntil);
            if (lockedUntil && lockedUntil.getTime() > Date.now()) {
                return { key: 'locked', label: `Locked until ${lockedUntil.toLocaleString()}` };
            }
            return { key: 'active', label: 'Active' };
        }

        function isActiveAdministrator(user) {
            return user.role === 'Admin' && !!user.isActive;
        }

        function isLastActiveAdministrator(user) {
            return isActiveAdministrator(user) && userAccounts.filter(isActiveAdministrator).length === 1;
        }

        function userAccountDomId(userId) {
            return 'user-account-' + String(userId).replace(/[^a-z0-9_-]/gi, '-');
        }

        function userDisplayName(user) {
            return user.fullName ? `${user.username} (${user.fullName})` : user.username;
        }

        // The administrator confirms these actions with their own password,
        // unless they signed in through Authelia and have none here.
        function needsUserAdminPassword() {
            return currentUserAuthMethod !== 'authelia';
        }

        /**
         * Which of the account's actions the server would refuse, as the ids
         * of the visible notes that say why (for aria-describedby), and the
         * notes this account's card shows. Authelia's reason is the page
         * notice above the list.
         */
        function userAccountRefusals(user) {
            const notes = [];
            const refusals = { role: [], password: [], active: [], remove: [] };
            const refuse = (noteId, actions) => actions.forEach(action => refusals[action].push(noteId));
            if (user.isCurrentUser) {
                const id = userAccountDomId(user.id) + '-own';
                notes.push({ id, text: 'This is your account. You cannot change its role, disable it or delete it here. To change your password, use Change Password in the account menu.' });
                refuse(id, ['role', 'password', 'active', 'remove']);
            } else if (isLastActiveAdministrator(user)) {
                const id = userAccountDomId(user.id) + '-last-admin';
                notes.push({ id, text: 'This is the only active administrator. Make another account an administrator before you change its role, disable it or delete it.' });
                refuse(id, ['role', 'active', 'remove']);
            }
            if (usersAutheliaEnabled) refuse('usersAutheliaNotice', ['role', 'password']);
            return { notes, refusals };
        }

        // A refused action's button is disabled and described by the notes
        // that say why, so the reason is on screen and read with the button.
        function userActionRefusal(noteIds) {
            return noteIds.length ? html` disabled aria-describedby="${noteIds.join(' ')}"` : '';
        }

        function renderUserAccount(user) {
            const id = userAccountDomId(user.id);
            const status = userAccountStatus(user);
            const { notes, refusals } = userAccountRefusals(user);
            return html`
                <li class="settings-card user-account" id="${id}" data-user-id="${user.id}">
                    <header class="settings-card-header">
                        <span class="settings-card-icon" aria-hidden="true">${uiIcon('user')}</span>
                        <div class="user-account-heading">
                            <div class="user-account-title-row">
                                <h3 class="settings-card-title user-account-name" id="${id}-name" tabindex="-1">${user.username}</h3>
                                ${user.isCurrentUser ? html`<span class="user-account-tag">You</span>` : ''}
                                <span class="user-account-status user-account-status--${status.key}" data-user-part="status"><span class="visually-hidden">Status: </span>${status.label}</span>
                            </div>
                            ${user.fullName ? html`<p class="settings-card-desc">${user.fullName}</p>` : ''}
                        </div>
                    </header>
                    <dl class="user-account-fields">
                        <div class="user-account-field"><dt>Email</dt><dd>${user.email || '—'}</dd></div>
                        <div class="user-account-field"><dt>Role</dt><dd>${userRoleLabel(user.role)}</dd></div>
                        <div class="user-account-field"><dt>Last sign-in</dt><dd>${formatUserDate(user.lastLoginAt, 'Never')}</dd></div>
                        <div class="user-account-field"><dt>Created</dt><dd>${formatUserDate(user.createdAt, '—')}</dd></div>
                        <div class="user-account-field"><dt>API key</dt><dd>${user.hasApiKey ? 'Yes' : 'None'}</dd></div>
                    </dl>
                    <div class="user-account-actions">
                        <button type="button" class="btn btn-small" data-user-action="role" onclick="openUserRoleModal('${jsArg(user.id)}')"${userActionRefusal(refusals.role)}>${uiIcon('user')} Change role</button>
                        <button type="button" class="btn btn-small" data-user-action="password" onclick="openUserPasswordModal('${jsArg(user.id)}')"${userActionRefusal(refusals.password)}>${uiIcon('key')} Reset password</button>
                        ${user.isActive
                            ? html`<button type="button" class="btn btn-small" data-user-action="disable" onclick="openUserConfirmModal('${jsArg(user.id)}', 'disable')"${userActionRefusal(refusals.active)}>${uiIcon('lock')} Disable</button>`
                            : html`<button type="button" class="btn btn-small" data-user-action="enable" onclick="openUserConfirmModal('${jsArg(user.id)}', 'enable')"${userActionRefusal(refusals.active)}>${uiIcon('check')} Enable</button>`}
                        <button type="button" class="btn btn-small btn-danger" data-user-action="delete" onclick="openUserConfirmModal('${jsArg(user.id)}', 'delete')"${userActionRefusal(refusals.remove)}>${uiIcon('trash')} Delete</button>
                    </div>
                    ${notes.map(note => html`<p class="form-hint user-account-note" id="${note.id}">${note.text}</p>`)}
                </li>
            `;
        }

        function renderUsersList() {
            const list = document.getElementById('usersList');
            if (!list) return;
            if (!userAccounts.length) {
                renderHtml(list, html`<div class="empty-state"><p>There are no accounts.</p></div>`);
                return;
            }
            const count = userAccounts.length;
            renderHtml(list, html`
                <p class="form-hint users-count">${count} ${count === 1 ? 'account' : 'accounts'}</p>
                <ul class="users-list-items" aria-label="Accounts">${userAccounts.map(renderUserAccount)}</ul>
            `);
        }

        // Add User is refused while Authelia creates the accounts; the notice
        // beside the button says so.
        function renderUsersToolbar() {
            const toolbar = document.getElementById('usersToolbar');
            if (toolbar) toolbar.hidden = false;
            const notice = document.getElementById('usersAutheliaNotice');
            if (notice) notice.hidden = !usersAutheliaEnabled;
            const addButton = document.getElementById('addUserButton');
            if (!addButton) return;
            addButton.disabled = usersAutheliaEnabled;
            if (usersAutheliaEnabled) {
                addButton.setAttribute('aria-describedby', 'usersAutheliaNotice');
            } else {
                addButton.removeAttribute('aria-describedby');
            }
        }

        // The users API is administrator-only; applyCapabilities hides the
        // entry points from everyone else. Anyone who still reaches the page
        // (an old link, or before their capabilities have loaded) gets a
        // notice in place of the accounts.
        function applyUsersCapabilities() {
            if (!userCapabilities.canAdminister) showUsersAdminOnly();
        }

        function showUsersAdminOnly() {
            ++usersLoadRequest;
            userAccounts = [];
            closeUserDialogs();
            const notice = document.getElementById('usersAdminNotice');
            if (notice) notice.hidden = false;
            const toolbar = document.getElementById('usersToolbar');
            if (toolbar) toolbar.hidden = true;
            const list = document.getElementById('usersList');
            if (list) {
                list.hidden = true;
                list.setAttribute('aria-busy', 'false');
                clearChildren(list);
            }
        }

        /**
         * Load and show the accounts. Resolves to true once they are on
         * screen, and to false when they are not (a failure, the notice, or a
         * newer load that superseded this one).
         */
        async function loadUsers() {
            const list = document.getElementById('usersList');
            if (!list) return false;
            if (!userCapabilities.canAdminister) {
                showUsersAdminOnly();
                return false;
            }
            const loadRequest = ++usersLoadRequest;
            const notice = document.getElementById('usersAdminNotice');
            if (notice) notice.hidden = true;
            list.hidden = false;
            list.setAttribute('aria-busy', 'true');
            if (!list.querySelector('[data-user-id]')) {
                renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Loading users...</p></div>`);
            }
            let shown = false;
            try {
                const data = await fetchUsersApi('/api/users');
                if (loadRequest !== usersLoadRequest) return false;
                // Anything but the expected object (e.g. from a misconfigured
                // proxy) reads as no accounts.
                const answer = data || {};
                userAccounts = Array.isArray(answer.users) ? answer.users : [];
                usersAutheliaEnabled = answer.autheliaEnabled === true;
                renderUsersToolbar();
                renderUsersList();
                shown = true;
            } catch (error) {
                if (loadRequest !== usersLoadRequest || error.authHandled) return false;
                if (error.status === 403) {
                    showUsersAdminOnly();
                    return false;
                }
                console.error('Failed to load users:', error);
                userAccounts = [];
                renderHtml(list, html`
                    <div class="empty-state">
                        <p>Users could not be loaded: ${withApiFailureReference(error.message, error)}</p>
                        <button type="button" class="btn" onclick="loadUsers()">${uiIcon('refresh')} Retry</button>
                    </div>
                `);
            } finally {
                if (loadRequest === usersLoadRequest) list.setAttribute('aria-busy', 'false');
            }
            return shown;
        }

        // After a change, show the list as the server now has it and put the
        // keyboard back on the account (its card was rendered afresh, so the
        // button that opened the dialog is gone), or on the page title.
        async function refreshUsersAndFocus(userId) {
            if (!await loadUsers()) return;
            const heading = userId ? document.getElementById(userAccountDomId(userId) + '-name') : null;
            const target = heading || document.getElementById('usersPageTitle');
            if (target) target.focus();
        }

        function findUserAccount(userId) {
            return userAccounts.find(user => user.id === userId) || null;
        }

        // ── Dialog helpers ───────────────────────────────────────────────

        function showUserDialogError(errorId, message) {
            const element = document.getElementById(errorId);
            element.textContent = message;
            element.classList.toggle('show', !!message);
        }

        function resetUserDialog(modalId, errorId, submitId) {
            const modal = document.getElementById(modalId);
            modal.querySelector('form').reset();
            modal.querySelectorAll('[aria-invalid]').forEach(element => element.removeAttribute('aria-invalid'));
            showUserDialogError(errorId, '');
            document.getElementById(submitId).disabled = false;
            return modal;
        }

        function clearUserDialogErrors(modalId, errorId) {
            document.getElementById(modalId).querySelectorAll('[aria-invalid]').forEach(element => element.removeAttribute('aria-invalid'));
            showUserDialogError(errorId, '');
        }

        // Show why a field was refused, mark it and put the keyboard on it.
        function refuseUserField(errorId, inputId, message) {
            const input = document.getElementById(inputId);
            showUserDialogError(errorId, message);
            input.setAttribute('aria-invalid', 'true');
            input.focus();
        }

        function userPasswordProblem(password) {
            const valid = password.length >= 8 && /[A-Z]/.test(password) && /[a-z]/.test(password) && /[0-9]/.test(password);
            return valid ? null : USER_PASSWORD_RULE;
        }

        // The "Your password" field: shown unless the administrator signed in
        // through Authelia, and required when shown.
        function showUserAdminPasswordField(groupId, shown = needsUserAdminPassword()) {
            document.getElementById(groupId).hidden = !shown;
        }

        // The administrator's own password, or a refusal when it is needed and
        // missing. Resolves to undefined when it is not needed.
        function readUserAdminPassword(errorId, inputId) {
            const input = document.getElementById(inputId);
            if (input.closest('[hidden]')) return { ok: true, value: undefined };
            if (!input.value) {
                refuseUserField(errorId, inputId, 'Enter your own password to confirm.');
                return { ok: false };
            }
            return { ok: true, value: input.value };
        }

        // A request from a dialog failed: say why in the dialog, which stays
        // open. A wrong or missing password of the administrator's own (403
        // from the server) is cleared for retyping. A 404 or 409 means the
        // list is out of date, so it is reloaded behind the dialog.
        function showUserDialogFailure(error, errorId, adminPasswordId) {
            if (error.authHandled) return;
            showUserDialogError(errorId, withApiFailureReference(error.message, error));
            const adminPassword = document.getElementById(adminPasswordId);
            if (error.status === 403 && error.fromServer && !adminPassword.closest('[hidden]')) {
                adminPassword.value = '';
                adminPassword.setAttribute('aria-invalid', 'true');
                adminPassword.focus();
            }
            if (error.status === 404 || error.status === 409) loadUsers();
        }

        function renderUserRoleOptions(containerId, name, selected) {
            renderHtml(document.getElementById(containerId), html`${USER_ROLES.map(role => html`
                <div class="users-role-option">
                    <input type="radio" name="${name}" id="${name}-${role.value}" value="${role.value}" aria-describedby="${name}-${role.value}-desc"${role.value === selected ? ' checked' : ''}>
                    <label for="${name}-${role.value}">${role.label}</label>
                    <span class="form-hint" id="${name}-${role.value}-desc">${role.description}</span>
                </div>
            `)}`);
        }

        function selectedUserRole(name) {
            const checked = document.querySelector(`input[name="${name}"]:checked`);
            return checked ? checked.value : DEFAULT_USER_ROLE;
        }

        function closeUserDialog(modalId) {
            document.getElementById(modalId).classList.remove('active');
        }

        // Close every Users dialog, e.g. when the page is left.
        function closeUserDialogs() {
            USER_DIALOG_IDS.forEach(id => {
                const modal = document.getElementById(id);
                if (modal) modal.classList.remove('active');
            });
            userDialogAccount = null;
            userConfirmAction = null;
        }

        function userAccountPath(user, action = '') {
            return `/api/users/${encodeURIComponent(user.id)}${action}`;
        }

        // ── Add user ─────────────────────────────────────────────────────

        function openAddUserModal() {
            const modal = resetUserDialog('addUserModal', 'addUserError', 'addUserSubmit');
            renderUserRoleOptions('addUserRoleOptions', 'addUserRole', DEFAULT_USER_ROLE);
            showUserAdminPasswordField('addUserCurrentPasswordGroup');
            modal.classList.add('active');
        }

        function closeAddUserModal() {
            closeUserDialog('addUserModal');
        }

        /** The new account from the Add User form, or null after showing why it cannot be added. */
        function readAddUserForm() {
            const value = id => document.getElementById(id).value;
            const username = value('addUserUsername').trim();
            const email = value('addUserEmail').trim();
            const password = value('addUserPassword');
            if (username.length < 3) {
                refuseUserField('addUserError', 'addUserUsername', 'Enter a username of at least 3 characters.');
                return null;
            }
            if (email && !USER_EMAIL_PATTERN.test(email)) {
                refuseUserField('addUserError', 'addUserEmail', 'Enter a valid email address, or leave the email empty.');
                return null;
            }
            const passwordProblem = userPasswordProblem(password);
            if (passwordProblem) {
                refuseUserField('addUserError', 'addUserPassword', passwordProblem);
                return null;
            }
            if (password !== value('addUserConfirmPassword')) {
                refuseUserField('addUserError', 'addUserConfirmPassword', 'The passwords do not match.');
                return null;
            }
            const adminPassword = readUserAdminPassword('addUserError', 'addUserCurrentPassword');
            if (!adminPassword.ok) return null;
            return {
                username,
                password,
                email: email || null,
                fullName: value('addUserFullName').trim() || null,
                role: selectedUserRole('addUserRole'),
                currentPassword: adminPassword.value
            };
        }

        async function submitAddUser() {
            const submit = document.getElementById('addUserSubmit');
            if (submit.disabled) return;
            clearUserDialogErrors('addUserModal', 'addUserError');
            const account = readAddUserForm();
            if (!account) return;
            submit.disabled = true;
            try {
                const created = await fetchUsersApi('/api/users', { method: 'POST', body: account });
                closeAddUserModal();
                showMessage(`Added the account ${account.username}.`, 'success');
                await refreshUsersAndFocus(created.id);
            } catch (error) {
                showUserDialogFailure(error, 'addUserError', 'addUserCurrentPassword');
            } finally {
                submit.disabled = false;
            }
        }

        // ── Change role ──────────────────────────────────────────────────

        function openUserRoleModal(userId) {
            const user = findUserAccount(userId);
            if (!user) return;
            userDialogAccount = user;
            const modal = resetUserDialog('userRoleModal', 'userRoleError', 'userRoleSubmit');
            renderHtml(document.getElementById('userRoleIntro'), html`Choose what <strong>${userDisplayName(user)}</strong> can do. Changing the role signs the account out everywhere, so it signs in again with the new role.`);
            renderUserRoleOptions('userRoleOptions', 'userRoleChoice', user.role || DEFAULT_USER_ROLE);
            showUserAdminPasswordField('userRoleCurrentPasswordGroup');
            modal.classList.add('active');
            // Start on the account's current role rather than the first one.
            // Deferred, so the dialog helper records what had focus first.
            setTimeout(() => {
                if (modal.classList.contains('active')) modal.querySelector('input[name="userRoleChoice"]:checked').focus();
            }, 0);
        }

        function closeUserRoleModal() {
            closeUserDialog('userRoleModal');
            userDialogAccount = null;
        }

        async function submitUserRole() {
            const user = userDialogAccount;
            const submit = document.getElementById('userRoleSubmit');
            if (!user || submit.disabled) return;
            clearUserDialogErrors('userRoleModal', 'userRoleError');
            const role = selectedUserRole('userRoleChoice');
            if (role === user.role) {
                closeUserRoleModal();
                showMessage(`${user.username} already has the role ${userRoleLabel(role)}.`, 'info');
                return;
            }
            const adminPassword = readUserAdminPassword('userRoleError', 'userRoleCurrentPassword');
            if (!adminPassword.ok) return;
            submit.disabled = true;
            try {
                await fetchUsersApi(userAccountPath(user, '/role'), { method: 'PUT', body: { role, currentPassword: adminPassword.value } });
                closeUserRoleModal();
                showMessage(`${user.username} now has the role ${userRoleLabel(role)}. Their sessions have ended, so they sign in again.`, 'success');
                await refreshUsersAndFocus(user.id);
            } catch (error) {
                showUserDialogFailure(error, 'userRoleError', 'userRoleCurrentPassword');
            } finally {
                submit.disabled = false;
            }
        }

        // ── Reset password ───────────────────────────────────────────────

        function openUserPasswordModal(userId) {
            const user = findUserAccount(userId);
            if (!user) return;
            userDialogAccount = user;
            const modal = resetUserDialog('userPasswordModal', 'userPasswordError', 'userPasswordSubmit');
            renderHtml(document.getElementById('userPasswordIntro'), html`Set a new password for <strong>${userDisplayName(user)}</strong>. This signs the account out everywhere, revokes its API key and lifts a lockout. Tell them the new password; they can change it with Change Password.`);
            showUserAdminPasswordField('userPasswordCurrentPasswordGroup');
            modal.classList.add('active');
        }

        function closeUserPasswordModal() {
            closeUserDialog('userPasswordModal');
            userDialogAccount = null;
        }

        async function submitUserPassword() {
            const user = userDialogAccount;
            const submit = document.getElementById('userPasswordSubmit');
            if (!user || submit.disabled) return;
            clearUserDialogErrors('userPasswordModal', 'userPasswordError');
            const newPassword = document.getElementById('userPasswordNew').value;
            const passwordProblem = userPasswordProblem(newPassword);
            if (passwordProblem) {
                refuseUserField('userPasswordError', 'userPasswordNew', passwordProblem);
                return;
            }
            if (newPassword !== document.getElementById('userPasswordConfirm').value) {
                refuseUserField('userPasswordError', 'userPasswordConfirm', 'The passwords do not match.');
                return;
            }
            const adminPassword = readUserAdminPassword('userPasswordError', 'userPasswordCurrentPassword');
            if (!adminPassword.ok) return;
            submit.disabled = true;
            try {
                await fetchUsersApi(userAccountPath(user, '/password'), { method: 'POST', body: { newPassword, currentPassword: adminPassword.value } });
                closeUserPasswordModal();
                showMessage(`Reset the password of ${user.username}. The account is signed out everywhere and its API key is revoked.`, 'success');
                await refreshUsersAndFocus(user.id);
            } catch (error) {
                showUserDialogFailure(error, 'userPasswordError', 'userPasswordCurrentPassword');
            } finally {
                submit.disabled = false;
            }
        }

        // ── Disable, enable, delete ──────────────────────────────────────

        function renderUserConfirmMessage(action, user) {
            const name = userDisplayName(user);
            if (action === 'disable') {
                return html`<p>Disable <strong>${name}</strong>? The account is signed out everywhere, its API key is revoked, and it cannot sign in until it is enabled again. Its reading progress, preferences and devices are kept.</p>`;
            }
            if (action === 'enable') {
                return html`<p>Enable <strong>${name}</strong>? The account can sign in again with its password. Its sessions and API key from before it was disabled stay revoked.</p>`;
            }
            return html`
                <p>Delete <strong>${name}</strong>? This cannot be undone. It deletes:</p>
                <ul>
                    <li>the account, its sign-in sessions and its API key</li>
                    <li>its reading progress and read status</li>
                    <li>its reader and app preferences</li>
                    <li>its e-reader devices, with their series subscriptions</li>
                    <li>its email delivery history</li>
                </ul>
                <p>Comic files are not touched.</p>
                ${usersAutheliaEnabled ? html`<p>Authelia is enabled: if this person signs in through Authelia again, a new, empty account is created for them. To keep them out, disable the account instead, or remove them in Authelia.</p>` : ''}
            `;
        }

        function openUserConfirmModal(userId, action) {
            const user = findUserAccount(userId);
            const config = USER_CONFIRM_ACTIONS[action];
            if (!user || !config) return;
            userDialogAccount = user;
            userConfirmAction = action;
            const modal = resetUserDialog('userConfirmModal', 'userConfirmError', 'userConfirmSubmit');
            document.getElementById('userConfirmModalTitle').textContent = config.title;
            renderHtml(document.getElementById('userConfirmMessage'), renderUserConfirmMessage(action, user));
            showUserAdminPasswordField('userConfirmCurrentPasswordGroup', config.needsPassword && needsUserAdminPassword());
            const submit = document.getElementById('userConfirmSubmit');
            submit.textContent = config.button;
            submit.classList.toggle('btn-danger', config.danger);
            submit.classList.toggle('btn-primary', !config.danger);
            modal.classList.add('active');
        }

        function closeUserConfirmModal() {
            closeUserDialog('userConfirmModal');
            userDialogAccount = null;
            userConfirmAction = null;
        }

        async function submitUserConfirm() {
            const user = userDialogAccount;
            const action = userConfirmAction;
            const submit = document.getElementById('userConfirmSubmit');
            if (!user || !action || submit.disabled) return;
            clearUserDialogErrors('userConfirmModal', 'userConfirmError');
            const adminPassword = readUserAdminPassword('userConfirmError', 'userConfirmCurrentPassword');
            if (!adminPassword.ok) return;
            submit.disabled = true;
            try {
                if (action === 'delete') {
                    await fetchUsersApi(userAccountPath(user), { method: 'DELETE' });
                } else {
                    await fetchUsersApi(userAccountPath(user, '/active'), { method: 'PUT', body: { isActive: action === 'enable', currentPassword: adminPassword.value } });
                }
                closeUserConfirmModal();
                const done = { disable: `Disabled the account ${user.username}. It is signed out everywhere.`, enable: `Enabled the account ${user.username}.`, delete: `Deleted the account ${user.username}.` };
                showMessage(done[action], 'success');
                await refreshUsersAndFocus(action === 'delete' ? null : user.id);
            } catch (error) {
                showUserDialogFailure(error, 'userConfirmError', 'userConfirmCurrentPassword');
            } finally {
                submit.disabled = false;
            }
        }
