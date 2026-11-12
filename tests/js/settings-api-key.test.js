'use strict';

// The API key control in the OPDS section of the settings page (js/app/settings.js): what it says about the
// signed-in user's key, creating and replacing one, and the password step that Authelia sessions skip.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { bootSettings, lastMessage, messages, callsTo, answerConfirm } = require('./support/settings-helpers');

const KEY_STATUS = 'GET /api/auth/api-key';
const CREATE = 'POST /api/auth/api-key';
const NEW_KEY = 'cm_a-brand-new-key';
const CREATED = '2026-01-02T03:04:05Z';
const USED = '2026-02-03T04:05:06Z';

const noKey = { hasApiKey: false, createdAt: null, lastUsedAt: null, header: 'X-API-Key' };
const hasKey = (lastUsedAt = null) => ({ hasApiKey: true, createdAt: CREATED, lastUsedAt, header: 'X-API-Key' });

// A key that exists from the moment it is created, as the server would report it.
function keyRoutes() {
    let created = false;
    return {
        [KEY_STATUS]: () => created ? hasKey() : noKey,
        [CREATE]: () => {
            created = true;
            return { apiKey: NEW_KEY, header: 'X-API-Key' };
        }
    };
}

function statusText(page) {
    return page.$('#opdsApiKeyStatus').textContent;
}

function buttonText(page) {
    return page.$('#opdsApiKeyButton').textContent;
}

function shownKey(page) {
    return page.$('#opdsApiKeyResult').hidden ? null : page.$('#opdsApiKeyValue').value;
}

async function create(page) {
    page.click('#opdsApiKeyButton');
    await page.settle();
}

test('an account without a key is told so and is asked for its password', async () => {
    const page = await bootSettings({ routes: keyRoutes() });

    assert.equal(statusText(page), 'You have no API key yet.');
    assert.equal(buttonText(page), 'Create API key');
    assert.equal(page.$('#opdsApiKeyPasswordGroup').hidden, false);
    assert.equal(shownKey(page), null);
});

test('an account with a key sees when it was created and last used, and can replace it', async () => {
    const page = await bootSettings({ routes: { [KEY_STATUS]: hasKey(USED) } });

    assert.equal(
        statusText(page),
        `You have an API key, created ${new Date(CREATED).toLocaleString()}, last used ${new Date(USED).toLocaleString()}.`);
    assert.equal(buttonText(page), 'Replace API key');
});

test('a key that was never used says so', async () => {
    const page = await bootSettings({ routes: { [KEY_STATUS]: hasKey() } });

    assert.match(statusText(page), /, not used yet\.$/);
});

test('a status that cannot be read is reported on the card, without a toast', async () => {
    const page = await bootSettings({ routes: { [KEY_STATUS]: () => respond(500, { error: 'nope' }) } });

    assert.equal(statusText(page), 'Could not check whether you have an API key.');
    assert.deepEqual(messages(page).filter(message => message.type === 'error'), []);
});

test('creating a key sends the password and shows the key once', async () => {
    const page = await bootSettings({ routes: keyRoutes() });
    page.input('#opdsApiKeyPassword', 'correct horse');

    await create(page);

    assert.deepEqual(callsTo(page, CREATE).map(call => call.body), [{ currentPassword: 'correct horse' }]);
    assert.equal(shownKey(page), NEW_KEY);
    assert.equal(page.$('#opdsApiKeyPassword').value, '');
    assert.match(statusText(page), /^You have an API key, created /);
    assert.equal(buttonText(page), 'Replace API key');
    assert.equal(page.$('#opdsApiKeyButton').disabled, false);
    assert.equal(lastMessage(page).type, 'success');
});

test('without a password nothing is sent', async () => {
    const page = await bootSettings({ routes: keyRoutes() });

    await create(page);

    assert.deepEqual(callsTo(page, CREATE), []);
    assert.deepEqual(lastMessage(page), { type: 'error', text: 'Enter your current password to create an API key.' });
    assert.equal(shownKey(page), null);
});

test('an Authelia session is not asked for a password', async () => {
    const page = await bootSettings({
        localStorage: { authelia_authenticated: 'true', username: 'bob' },
        routes: {
            ...keyRoutes(),
            'GET /api/auth/status': { autheliaEnabled: true, isAuthenticated: true, username: 'bob', authMethod: 'authelia', requiresLogin: false }
        }
    });

    assert.equal(page.$('#opdsApiKeyPasswordGroup').hidden, true);

    await create(page);

    assert.deepEqual(callsTo(page, CREATE).map(call => call.body), [{}]);
    assert.equal(shownKey(page), NEW_KEY);
});

test('replacing a key asks first, and declining sends nothing', async () => {
    const page = await bootSettings({ routes: { [KEY_STATUS]: hasKey(USED), [CREATE]: { apiKey: NEW_KEY } } });
    page.input('#opdsApiKeyPassword', 'correct horse');

    answerConfirm(page, false);
    await create(page);

    assert.deepEqual(callsTo(page, CREATE), []);
    assert.match(page.dialogs.at(-1).message, /^Replace your API key\?/);
    assert.equal(page.$('#opdsApiKeyPassword').value, 'correct horse');

    answerConfirm(page, true);
    await create(page);

    assert.equal(callsTo(page, CREATE).length, 1);
    assert.equal(shownKey(page), NEW_KEY);
});

test('a refused password shows the reason and keeps what was typed', async () => {
    const page = await bootSettings({
        routes: { [CREATE]: () => respond(403, { error: 'The current password is incorrect.' }) }
    });
    page.input('#opdsApiKeyPassword', 'wrong');

    await create(page);

    assert.deepEqual(lastMessage(page), { type: 'error', text: 'Could not create the API key: The current password is incorrect.' });
    assert.equal(shownKey(page), null);
    assert.equal(page.$('#opdsApiKeyPassword').value, 'wrong');
    assert.equal(page.$('#opdsApiKeyButton').disabled, false);
});

test('a session that ended is left to the sign-in redirect', async () => {
    const page = await bootSettings({ routes: { [CREATE]: () => respond(401, {}) } });
    page.input('#opdsApiKeyPassword', 'correct horse');

    await create(page);

    assert.deepEqual(messages(page).filter(message => message.text.startsWith('Could not create')), []);
    assert.equal(shownKey(page), null);
});

test('someone who cannot change settings can still make their own key', async () => {
    const page = await bootSettings({
        user: { roles: ['ReadOnly'], canModifyLibrary: false, canAdminister: false },
        routes: keyRoutes()
    });

    assert.equal(page.$('#opdsEnabledCheckbox').disabled, true);
    assert.equal(page.$('#opdsApiKeyPassword').disabled, false);
    assert.equal(page.$('#opdsApiKeyButton').disabled, false);
});

test('a new key survives a settings save but not leaving the page', async () => {
    const page = await bootSettings({ routes: { ...keyRoutes(), 'PUT /api/settings': {} } });
    page.input('#opdsApiKeyPassword', 'correct horse');
    await create(page);
    assert.equal(shownKey(page), NEW_KEY);

    page.click('#saveSettingsBtn');
    await page.settle();
    assert.equal(shownKey(page), NEW_KEY);

    page.window.location.hash = '#/home';
    await page.settle();
    page.window.location.hash = '#/settings';
    await page.settle();

    assert.equal(shownKey(page), null);
    assert.equal(page.$('#opdsApiKeyValue').value, '');
});
