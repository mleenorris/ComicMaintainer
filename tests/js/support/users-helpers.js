'use strict';

// Shared setup for the users page tests (tests/js/users-*.test.js): boots the
// signed-in SPA on #/users against a small in-memory users API, and reads the
// account cards and dialogs back as plain data.

const { boot, messages, requests } = require('./core-helpers');

const USERS = '/api/users';

// An account as GET /api/users lists it (UserAccountSummary).
function account(overrides = {}) {
    return {
        id: 'u-bob',
        username: 'bob',
        fullName: 'Bob Builder',
        email: 'bob@example.com',
        role: 'User',
        isActive: true,
        lockedOutUntil: null,
        createdAt: '2026-01-02T03:04:05Z',
        lastLoginAt: '2026-03-04T05:06:07Z',
        hasApiKey: false,
        isCurrentUser: false,
        ...overrides
    };
}

// The signed-in administrator (bootRoutes signs in "alice").
const ME = account({ id: 'u-alice', username: 'alice', fullName: 'Alice Admin', email: 'alice@example.com', role: 'Admin', isCurrentUser: true });
const BOB = account();

/**
 * Boot on `hash` (default #/users) with GET /api/users answered from
 * `page.server` ({ users, autheliaEnabled }), which a test may change before
 * the next load. `routes`, `user` and other options go to boot().
 */
async function bootUsers({ users = [ME, BOB], autheliaEnabled = false, hash = '#/users', routes = {}, ...options } = {}) {
    const server = { users, autheliaEnabled };
    const page = await boot({
        hash,
        routes: { [`GET ${USERS}`]: () => ({ autheliaEnabled: server.autheliaEnabled, users: server.users }), ...routes },
        ...options
    });
    page.server = server;
    return page;
}

const text = element => (element ? element.textContent.replace(/\s+/g, ' ').trim() : null);

function card(page, userId) {
    return page.$$('#usersList [data-user-id]').find(element => element.dataset.userId === userId) || null;
}

// The ids of the listed accounts, in order.
function cardIds(page) {
    return page.$$('#usersList [data-user-id]').map(element => element.dataset.userId);
}

function actionView(button) {
    if (!button) return null;
    return { label: text(button), disabled: button.disabled, describedBy: button.getAttribute('aria-describedby') };
}

// What an account's card shows, as plain data.
function cardView(page, userId) {
    const element = card(page, userId);
    if (!element) return null;
    const status = element.querySelector('[data-user-part="status"]');
    return {
        name: text(element.querySelector('h3')),
        you: !!element.querySelector('.user-account-tag'),
        fullName: text(element.querySelector('.settings-card-desc')),
        status: text(status).replace(/^Status: /, ''),
        statusClass: [...status.classList].find(name => name.startsWith('user-account-status--')),
        fields: Object.fromEntries([...element.querySelectorAll('.user-account-field')]
            .map(field => [text(field.querySelector('dt')), text(field.querySelector('dd'))])),
        actions: Object.fromEntries([...element.querySelectorAll('[data-user-action]')]
            .map(button => [button.dataset.userAction, actionView(button)])),
        notes: [...element.querySelectorAll('.user-account-note')].map(note => ({ id: note.id, text: text(note) }))
    };
}

/** Click one of an account's action buttons and let the dialog open. */
async function clickAction(page, userId, action) {
    const button = card(page, userId).querySelector(`[data-user-action="${action}"]`);
    // A click focuses the button in a browser; jsdom's click() does not.
    button.focus();
    page.click(button);
    await page.settle();
}

const isOpen = (page, modalId) => page.$(`#${modalId}`).classList.contains('active');

// The dialog's error box: its text when shown, else null.
function dialogError(page, errorId) {
    const element = page.$(`#${errorId}`);
    return element.classList.contains('show') ? element.textContent : null;
}

// Fill fields of a dialog: { inputId: value }.
function fill(page, values) {
    for (const [id, value] of Object.entries(values)) page.input(`#${id}`, value);
}

/** Submit a dialog with its footer button (which names the form) and let the request finish. */
async function submit(page, submitId) {
    page.click(`#${submitId}`);
    await page.settle();
}

// The role radio buttons of a dialog: [{ value, label, description, checked }].
function roleOptions(page, containerId) {
    return page.$$(`#${containerId} .users-role-option`).map(option => {
        const input = option.querySelector('input');
        const description = page.document.getElementById(input.getAttribute('aria-describedby'));
        return { value: input.value, label: text(option.querySelector('label')), description: text(description), checked: input.checked };
    });
}

/** The requests sent to /api/users and below with this method, as { path, body }. */
function userRequests(page, method) {
    return page.fetchCalls
        .filter(call => call.method === method && call.path.startsWith(USERS))
        .map(call => ({ path: call.path, body: call.body }));
}

// messages(), then removes them so the next step starts from none.
function takeMessages(page) {
    const shown = messages(page).map(({ type, text: messageText }) => ({ type, text: messageText }));
    page.$('#messageContainer').replaceChildren();
    return shown;
}

const listLoads = page => requests(page, 'GET', USERS).length;

module.exports = {
    USERS,
    ME,
    BOB,
    account,
    bootUsers,
    card,
    cardIds,
    cardView,
    clickAction,
    dialogError,
    fill,
    isOpen,
    listLoads,
    roleOptions,
    submit,
    takeMessages,
    text,
    userRequests
};
