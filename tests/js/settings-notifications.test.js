'use strict';

// Settings page: the Notifications section — loading each target, the
// "notifications" group of Save Settings (how stored secrets are kept,
// replaced and cleared), settings pinned by the environment, and Send Test.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { bootSettings, messages, clearMessages, callsTo } = require('./support/settings-helpers');

const SAVE_ROUTE = 'PUT /api/settings';

const NOTIFICATION_SETTINGS = {
    notify_ntfy_enabled: true,
    notify_ntfy_url: 'https://ntfy.example',
    notify_ntfy_topic: 'comics',
    notify_ntfy_token_set: true,
    notify_ntfy_events: 'new-chapters,job-failed',
    notify_gotify_enabled: false,
    notify_gotify_url: 'https://gotify.example',
    notify_gotify_token_set: false,
    notify_gotify_events: '',
    notify_discord_enabled: true,
    notify_discord_webhook_url_set: true,
    notify_discord_events: 'job-findings',
    notify_webhook_enabled: false,
    notify_webhook_url_set: false,
    notify_webhook_events: 'suwayomi-import-failed',
    notify_komga_enabled: true,
    notify_komga_url: 'http://komga.lan:25600',
    notify_komga_api_key_set: true,
    notify_komga_library_id: 'LIB123',
    notify_komga_events: 'library-changed',
    notify_kavita_enabled: false,
    notify_kavita_url: 'http://kavita.lan:5000',
    notify_kavita_api_key_set: false,
    notify_kavita_library_id: '',
    notify_kavita_events: 'new-chapters,library-changed'
};

function bootNotifications(settings = {}, options = {}) {
    return bootSettings({ settings: { ...NOTIFICATION_SETTINGS, ...settings }, ...options });
}

async function save(page) {
    const since = page.fetchCalls.length;
    clearMessages(page);
    page.click('#saveSettingsBtn');
    await page.settle();
    const [call] = callsTo(page, SAVE_ROUTE, since);
    return call.body.notifications;
}

function routeSave(page) {
    page.route(SAVE_ROUTE, { message: 'Settings saved' });
}

test('each target is filled from the settings the server reports', async () => {
    const page = await bootNotifications();

    assert.equal(page.$('#notifyNtfyEnabledCheckbox').checked, true);
    assert.equal(page.$('#notifyNtfyUrl').value, 'https://ntfy.example');
    assert.equal(page.$('#notifyNtfyTopic').value, 'comics');
    assert.equal(page.$('#notifyNtfyEventNewChapters').checked, true);
    assert.equal(page.$('#notifyNtfyEventSuwayomi').checked, false);
    assert.equal(page.$('#notifyNtfyEventJobFailed').checked, true);
    assert.equal(page.$('#notifyGotifyEnabledCheckbox').checked, false);
    assert.equal(page.$('#notifyGotifyUrl').value, 'https://gotify.example');
    assert.equal(page.$('#notifyGotifyEventJobFindings').checked, false);
    assert.equal(page.$('#notifyDiscordEventJobFindings').checked, true);
    assert.equal(page.$('#notifyWebhookEventSuwayomi').checked, true);
    assert.equal(page.$('#notifyKomgaUrl').value, 'http://komga.lan:25600');
    assert.equal(page.$('#notifyKomgaLibraryId').value, 'LIB123');
    assert.equal(page.$('#notifyKomgaEventNewChapters').checked, false);
    assert.equal(page.$('#notifyKomgaEventLibraryChanged').checked, true);
    assert.equal(page.$('#notifyKavitaEventNewChapters').checked, true);
});

test('an unset setting loads as empty, and events that are missing load unchecked', async () => {
    const page = await bootSettings({ settings: {} });

    assert.equal(page.$('#notifyNtfyEnabledCheckbox').checked, false);
    assert.equal(page.$('#notifyNtfyUrl').value, '');
    assert.equal(page.$('#notifyNtfyTopic').value, '');
    assert.equal(page.$('#notifyKomgaLibraryId').value, '');
    assert.equal(page.$('#notifyNtfyEventNewChapters').checked, false);
});

test('a stored secret is never shown, only whether one is kept', async () => {
    const page = await bootNotifications();

    assert.equal(page.$('#notifyNtfyToken').value, '');
    assert.equal(page.$('#notifyNtfyTokenHint').textContent, 'Leave blank to keep the stored value.');
    assert.equal(page.$('#notifyDiscordUrl').value, '');
    assert.equal(page.$('#notifyDiscordUrlHint').textContent, 'Leave blank to keep the stored value.');
    assert.match(page.$('#notifyGotifyTokenHint').textContent, /Required/);
    assert.match(page.$('#notifyWebhookUrlHint').textContent, /Paste the URL/);
});

test('Save sends each target; a blank secret keeps the stored one', async () => {
    const page = await bootNotifications();
    routeSave(page);

    const notifications = await save(page);

    assert.deepEqual(notifications.ntfy, {
        enabled: true,
        url: 'https://ntfy.example',
        events: 'new-chapters,job-failed',
        token: null,
        topic: 'comics'
    });
    assert.deepEqual(notifications.discord, { enabled: true, url: null, events: 'job-findings' });
    assert.deepEqual(notifications.komga, {
        enabled: true,
        url: 'http://komga.lan:25600',
        events: 'library-changed',
        token: null,
        libraryId: 'LIB123'
    });
    assert.deepEqual(Object.keys(notifications), ['ntfy', 'gotify', 'discord', 'webhook', 'komga', 'kavita']);
});

test('Save sends a new secret, trims text fields and rebuilds the event list', async () => {
    const page = await bootNotifications();
    routeSave(page);
    page.input('#notifyNtfyToken', ' tk_new ');
    page.input('#notifyNtfyUrl', '  https://other.example  ');
    page.input('#notifyNtfyTopic', ' other-topic ');
    page.input('#notifyNtfyEventSuwayomi', true);
    page.input('#notifyNtfyEventNewChapters', false);
    page.input('#notifyKavitaLibraryId', ' 4 ');

    const notifications = await save(page);

    assert.equal(notifications.ntfy.token, ' tk_new ', 'a secret is sent exactly as typed');
    assert.equal(notifications.ntfy.url, 'https://other.example');
    assert.equal(notifications.ntfy.topic, 'other-topic');
    assert.equal(notifications.ntfy.events, 'suwayomi-import-failed,job-failed');
    assert.equal(notifications.kavita.libraryId, '4');
});

test('Save sends a new webhook URL as a secret', async () => {
    const page = await bootNotifications();
    routeSave(page);
    page.input('#notifyDiscordUrl', 'https://discord.com/api/webhooks/1/new');

    assert.equal((await save(page)).discord.url, 'https://discord.com/api/webhooks/1/new');
});

test('The clear checkbox empties a secret, even with text in the field', async () => {
    const page = await bootNotifications();
    routeSave(page);
    page.input('#notifyNtfyToken', 'typed-then-cleared');
    page.input('#notifyNtfyClearTokenCheckbox', true);
    page.input('#notifyDiscordClearUrlCheckbox', true);
    page.input('#notifyKomgaClearTokenCheckbox', true);

    const notifications = await save(page);

    assert.equal(notifications.ntfy.token, '');
    assert.equal(notifications.discord.url, '');
    assert.equal(notifications.komga.token, '');
});

test('After a save the secret fields are empty again', async () => {
    const page = await bootNotifications();
    routeSave(page);
    page.input('#notifyGotifyToken', 'secret');
    page.input('#notifyGotifyClearTokenCheckbox', true);

    await save(page);

    assert.equal(page.$('#notifyGotifyToken').value, '');
    assert.equal(page.$('#notifyGotifyClearTokenCheckbox').checked, false);
});

test('A pinned setting is disabled with its variable named, and left out of the save', async () => {
    const page = await bootNotifications({
        env_locked: [
            { field: 'notify_ntfy_url', variable: 'NOTIFY_NTFY_URL' },
            { field: 'notify_ntfy_token_set', variable: 'NOTIFY_NTFY_TOKEN' },
            { field: 'notify_ntfy_events', variable: 'NOTIFY_NTFY_EVENTS' },
            { field: 'notify_discord_webhook_url_set', variable: 'NOTIFY_DISCORD_WEBHOOK_URL' },
            { field: 'notify_komga_enabled', variable: 'NOTIFY_KOMGA_ENABLED' }
        ]
    });
    routeSave(page);

    assert.equal(page.$('#notifyNtfyUrl').disabled, true);
    assert.equal(page.$('#notifyNtfyToken').disabled, true);
    assert.equal(page.$('#notifyNtfyClearTokenCheckbox').disabled, true);
    assert.equal(page.$('#notifyNtfyEventNewChapters').disabled, true);
    assert.equal(page.$('#notifyNtfyEventJobFindings').disabled, true);
    assert.equal(page.$('#notifyDiscordUrl').disabled, true);
    assert.equal(page.$('#notifyKomgaEnabledCheckbox').disabled, true);
    assert.equal(page.$('#notifyNtfyTopic').disabled, false);
    assert.match(page.$('[data-env-lock-hint="notify_ntfy_url"]').textContent, /NOTIFY_NTFY_URL/);

    const notifications = await save(page);

    assert.deepEqual(notifications.ntfy, { enabled: true, topic: 'comics' }, 'url, token and events are left out');
    assert.deepEqual(notifications.discord, { enabled: true, events: 'job-findings' });
    assert.equal('enabled' in notifications.komga, false);
});

test('Send Test shows the answer next to the button', async () => {
    const page = await bootNotifications();
    const route = 'POST /api/notifications/ntfy/test';
    page.route(route, { message: 'Test sent to ntfy.' });

    await page.run("sendNotificationTest('ntfy')");
    await page.settle();

    assert.equal(page.$('#notifyNtfyTestResult').textContent, 'Test sent to ntfy.');
    assert.equal(callsTo(page, route).length, 1);
    assert.deepEqual(messages(page).at(-1), { type: 'success', text: 'Test sent to ntfy.' });
});

test('Send Test says when the server had nothing more to add', async () => {
    const page = await bootNotifications();
    page.route('POST /api/notifications/gotify/test', undefined);

    await page.run("sendNotificationTest('gotify')");
    await page.settle();

    assert.equal(page.$('#notifyGotifyTestResult').textContent, 'Test sent.');
});

test('Send Test shows the target\'s own error when it fails', async () => {
    const page = await bootNotifications();
    page.route('POST /api/notifications/komga/test', respond(502, { error: 'Komga answered with HTTP 401: <b>nope</b>' }));

    await page.run("sendNotificationTest('komga')");
    await page.settle();

    assert.equal(page.$('#notifyKomgaTestResult').textContent, 'Komga answered with HTTP 401: <b>nope</b>');
    assert.equal(page.$('#notifyKomgaTestResult').querySelector('b'), null, 'shown as text');
    assert.deepEqual(messages(page).at(-1), { type: 'error', text: 'Komga answered with HTTP 401: <b>nope</b>' });
});

test('Send Test falls back to the status when the server gives no reason', async () => {
    const page = await bootNotifications();
    page.route('POST /api/notifications/webhook/test', respond(500, 'oops'));

    await page.run("sendNotificationTest('webhook')");
    await page.settle();

    assert.equal(page.$('#notifyWebhookTestResult').textContent, 'Test failed (HTTP 500).');
});

test('Send Test reports a request that never got an answer', async () => {
    const page = await bootNotifications();
    page.route('POST /api/notifications/discord/test', () => {
        throw new TypeError('Failed to fetch');
    });

    await page.run("sendNotificationTest('discord')");
    await page.settle();

    assert.equal(page.$('#notifyDiscordTestResult').textContent, 'Could not send the test: Failed to fetch');
    assert.equal(messages(page).at(-1).type, 'error');
});

test('Send Test says nothing when the session has ended', async () => {
    const page = await bootNotifications();
    page.route('POST /api/notifications/kavita/test', 401);
    clearMessages(page);

    await page.run("sendNotificationTest('kavita')");
    await page.settle();

    assert.deepEqual(messages(page), []);
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
});

test('A page without the section still loads and saves', async () => {
    const page = await bootSettings({
        beforeScripts: window => window.document.getElementById('settings-notifications').remove()
    });
    page.route(SAVE_ROUTE, { message: 'Settings saved' });

    assert.equal(await save(page), undefined);
});
