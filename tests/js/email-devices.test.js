'use strict';

// js/app/email.js: the Ereader Devices modal (list, add, edit, delete, test
// email), the settings page's email status line and the shared JSON fetch
// helper's error handling.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const {
    emailDevice,
    emailStatus,
    bootPage,
    messages,
    messageTexts,
    isModalOpen,
    pressEscape,
    buttonByText,
    answerConfirms,
    callsTo
} = require('./support/email-helpers');

const KINDLE = emailDevice({ id: 1, name: 'Kindle', emailAddress: 'reader@kindle.com', deliveryFormat: 'azw3' });
const KOBO = emailDevice({ id: 2, name: 'Kobo', emailAddress: 'me@kobo.com', deliveryFormat: 'EPUB', updatedAt: null, createdAt: '2024-05-06T07:08:09Z' });

function openFromSidebar(page) {
    page.click('.sidebar-link[onclick="openEmailDevicesModal()"]');
}

function deviceCards(page) {
    return page.$$('#emailDevicesList .duplicate-review-card').map(card => ({
        name: card.querySelector('h3').textContent,
        email: card.querySelector('p').textContent,
        meta: [...card.querySelectorAll('.duplicate-review-meta strong')].map(element => element.textContent)
    }));
}

function cardButton(page, deviceName, label) {
    const card = page.$$('#emailDevicesList .duplicate-review-card')
        .find(element => element.querySelector('h3').textContent === deviceName);
    return buttonByText(page, card, label);
}

function formValues(page) {
    return {
        id: page.$('#emailDeviceId').value,
        title: page.$('#emailDeviceFormTitle').textContent,
        name: page.$('#emailDeviceName').value,
        address: page.$('#emailDeviceAddress').value,
        format: page.$('#emailDeviceFormat').value
    };
}

const EMPTY_FORM = { id: '', title: 'Add Device', name: '', address: '', format: 'original' };

test('the sidebar opens the devices modal, which loads and lists the saved devices', async t => {
    const devices = deferred();
    const page = await bootPage({ routes: { 'GET /api/email/devices': () => devices.promise } });
    t.after(() => page.close());
    page.input('#emailDeviceName', 'left over');

    openFromSidebar(page);

    assert.ok(isModalOpen(page, 'emailDevicesModal'));
    assert.equal(page.$('#emailDevicesList').textContent.trim(), 'Loading devices...');
    assert.deepEqual(formValues(page), EMPTY_FORM);

    const odd = emailDevice({ id: 3, name: 'Tablet', emailAddress: 'tab@example.com', deliveryFormat: 'device', updatedAt: 'not a date' });
    const bare = emailDevice({ id: 4, name: 'Phone', emailAddress: 'phone@example.com', deliveryFormat: null, updatedAt: null, createdAt: null });
    devices.resolve({ devices: [KINDLE, KOBO, odd, bare] });
    await page.settle();

    assert.deepEqual(deviceCards(page), [
        { name: 'Kindle', email: 'reader@kindle.com', meta: ['AZW3 (Kindle)', new Date(KINDLE.updatedAt).toLocaleString()] },
        { name: 'Kobo', email: 'me@kobo.com', meta: ['EPUB', new Date(KOBO.createdAt).toLocaleString()] },
        { name: 'Tablet', email: 'tab@example.com', meta: ['Device default', '—'] },
        { name: 'Phone', email: 'phone@example.com', meta: ['Original archive', '—'] }
    ]);
    const [call] = callsTo(page, 'GET', '/api/email/devices');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.equal(call.init.credentials, 'include');
});

test('the devices modal shows an empty state and load failures', async t => {
    const page = await bootPage({ routes: { 'GET /api/email/devices': { devices: [] } } });
    t.after(() => page.close());
    const listText = () => page.$('#emailDevicesList').textContent.trim();

    await page.run('openEmailDevicesModal()');
    assert.equal(listText(), 'No ereader devices saved yet.');

    page.route('GET /api/email/devices', { devices: null });
    await page.run('openEmailDevicesModal()');
    assert.equal(listText(), 'No ereader devices saved yet.');

    page.route('GET /api/email/devices', respond(500, { error: 'Database unavailable' }));
    await page.run('openEmailDevicesModal()');
    assert.equal(listText(), 'Failed to load devices: Database unavailable');

    page.route('GET /api/email/devices', respond(503, 'Service Unavailable'));
    await page.run('openEmailDevicesModal()');
    assert.equal(listText(), 'Failed to load devices: HTTP error! status: 503');

    page.route('GET /api/email/devices', () => { throw new TypeError('Failed to fetch'); });
    await page.run('openEmailDevicesModal()');
    assert.equal(listText(), 'Failed to load devices: Failed to fetch');
});

test('an expired session while loading devices goes to the login page', async t => {
    const page = await bootPage({ routes: { 'GET /api/email/devices': 401 } });
    t.after(() => page.close());

    await page.run('openEmailDevicesModal()');

    assert.equal(page.$('#emailDevicesList').textContent.trim(), 'Failed to load devices: Authentication required');
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
});

test('a 200 response without a JSON body counts as an empty list', async t => {
    const page = await bootPage({ routes: { 'GET /api/email/devices': respond(200, 'OK') } });
    t.after(() => page.close());

    await page.run('openEmailDevicesModal()');

    assert.equal(page.$('#emailDevicesList').textContent.trim(), 'No ereader devices saved yet.');
});

test('Save Device requires a name and an address', async t => {
    const page = await bootPage({ routes: { 'GET /api/email/devices': { devices: [] } } });
    t.after(() => page.close());
    await page.run('openEmailDevicesModal()');
    const save = () => page.run('saveEmailDevice()');

    await save();
    page.input('#emailDeviceName', 'Kindle');
    page.input('#emailDeviceAddress', '   ');
    await save();

    assert.deepEqual(messageTexts(page, 'error'), [
        'Device name and email address are required.',
        'Device name and email address are required.'
    ]);
    assert.equal(callsTo(page, 'POST', '/api/email/devices').length, 0);
});

test('Save Device adds a device, then resets the form and reloads the list', async t => {
    let saved = [];
    const page = await bootPage({
        routes: {
            'GET /api/email/devices': () => ({ devices: saved }),
            'POST /api/email/devices': call => {
                saved = [emailDevice({ id: 9, ...call.body })];
                return saved[0];
            }
        }
    });
    t.after(() => page.close());
    openFromSidebar(page);
    await page.settle();

    page.input('#emailDeviceName', '  Kobo Libra ');
    page.input('#emailDeviceAddress', ' me@kobo.com ');
    page.input('#emailDeviceFormat', 'epub');
    page.click('#emailDevicesModal .modal-footer .btn-primary');
    await page.settle();

    const [call] = callsTo(page, 'POST', '/api/email/devices');
    assert.deepEqual(call.body, { name: 'Kobo Libra', emailAddress: 'me@kobo.com', deliveryFormat: 'epub' });
    assert.equal(call.headers['content-type'], 'application/json');
    assert.deepEqual(messages(page), [{ text: 'Ereader device added.', type: 'success' }]);
    assert.deepEqual(formValues(page), EMPTY_FORM);
    assert.deepEqual(deviceCards(page).map(card => card.name), ['Kobo Libra']);
    assert.equal(callsTo(page, 'GET', '/api/email/devices').length, 2);
});

test('Save Device reports the server error and keeps the form', async t => {
    const page = await bootPage({ routes: { 'GET /api/email/devices': { devices: [] } } });
    t.after(() => page.close());
    await page.run('openEmailDevicesModal()');
    page.input('#emailDeviceName', 'Kindle');
    page.input('#emailDeviceAddress', 'reader@kindle.com');

    page.route('POST /api/email/devices', respond(409, { error: 'A device with this email address already exists.' }));
    await page.run('saveEmailDevice()');
    page.route('POST /api/email/devices', respond(400, { message: 'Email address is invalid.' }));
    await page.run('saveEmailDevice()');
    page.route('POST /api/email/devices', respond(500, {}));
    await page.run('saveEmailDevice()');

    assert.deepEqual(messageTexts(page, 'error'), [
        'Failed to save ereader device: A device with this email address already exists.',
        'Failed to save ereader device: Email address is invalid.',
        'Failed to save ereader device: HTTP error! status: 500'
    ]);
    assert.equal(formValues(page).name, 'Kindle');
    assert.equal(formValues(page).address, 'reader@kindle.com');
});

test('Edit fills the form with the device and Save sends a PUT', async t => {
    const tablet = emailDevice({ id: 3, name: 'Tablet', emailAddress: 'tab@example.com', deliveryFormat: 'device' });
    const page = await bootPage({
        routes: {
            'GET /api/email/devices': { devices: [KINDLE, tablet] },
            'PUT /api/email/devices/1': call => ({ ...KINDLE, ...call.body })
        }
    });
    t.after(() => page.close());
    await page.run('openEmailDevicesModal()');

    page.click(cardButton(page, 'Tablet', 'Edit'));
    assert.deepEqual(formValues(page), { id: '3', title: 'Edit Device', name: 'Tablet', address: 'tab@example.com', format: 'original' });

    page.click(cardButton(page, 'Kindle', 'Edit'));
    assert.deepEqual(formValues(page), { id: '1', title: 'Edit Device', name: 'Kindle', address: 'reader@kindle.com', format: 'azw3' });

    page.run("editEmailDevice('404')");
    assert.equal(formValues(page).id, '1', 'an unknown device leaves the form alone');

    page.input('#emailDeviceName', 'Kindle Oasis');
    page.click('#emailDevicesModal .modal-footer .btn-primary');
    await page.settle();

    const [call] = callsTo(page, 'PUT', '/api/email/devices/1');
    assert.deepEqual(call.body, { name: 'Kindle Oasis', emailAddress: 'reader@kindle.com', deliveryFormat: 'azw3' });
    assert.deepEqual(messages(page), [{ text: 'Ereader device updated.', type: 'success' }]);
    assert.deepEqual(formValues(page), EMPTY_FORM);
    assert.equal(callsTo(page, 'POST', '/api/email/devices').length, 0);
});

test('Clear empties the form', async t => {
    const page = await bootPage({ routes: { 'GET /api/email/devices': { devices: [KINDLE] } } });
    t.after(() => page.close());
    await page.run('openEmailDevicesModal()');
    page.click(cardButton(page, 'Kindle', 'Edit'));

    page.click(buttonByText(page, '#emailDevicesModal .modal-footer', 'Clear'));

    assert.deepEqual(formValues(page), EMPTY_FORM);
});

test('Delete asks first and removes the device when confirmed', async t => {
    let devices = [KINDLE, KOBO];
    const page = await bootPage({
        routes: {
            'GET /api/email/devices': () => ({ devices }),
            'DELETE /api/email/devices/1': () => {
                devices = [KOBO];
                return { message: 'Device deleted' };
            }
        }
    });
    t.after(() => page.close());
    await page.run('openEmailDevicesModal()');
    answerConfirms(page, false, true);

    page.click(cardButton(page, 'Kindle', 'Delete'));
    await page.settle();
    assert.deepEqual(page.dialogs, [{ kind: 'confirm', message: 'Delete this ereader device?' }]);
    assert.equal(callsTo(page, 'DELETE', '/api/email/devices/1').length, 0);

    page.click(cardButton(page, 'Kindle', 'Delete'));
    await page.settle();

    assert.equal(callsTo(page, 'DELETE', '/api/email/devices/1').length, 1);
    assert.deepEqual(messages(page), [{ text: 'Ereader device deleted.', type: 'success' }]);
    assert.deepEqual(deviceCards(page).map(card => card.name), ['Kobo']);
});

test('a failed delete is reported and the list is kept', async t => {
    const page = await bootPage({
        routes: {
            'GET /api/email/devices': { devices: [KINDLE] },
            'DELETE /api/email/devices/1': respond(404, { error: 'Device not found' })
        }
    });
    t.after(() => page.close());
    await page.run('openEmailDevicesModal()');

    page.click(cardButton(page, 'Kindle', 'Delete'));
    await page.settle();

    assert.deepEqual(messageTexts(page, 'error'), ['Failed to delete ereader device: Device not found']);
    assert.deepEqual(deviceCards(page).map(card => card.name), ['Kindle']);
});

test('Send Test Email queues a test message and reports failures', async t => {
    const page = await bootPage({
        routes: {
            'GET /api/email/devices': { devices: [KINDLE] },
            'POST /api/email/devices/1/test': { message: 'Test email sent' }
        }
    });
    t.after(() => page.close());
    await page.run('openEmailDevicesModal()');

    page.click(cardButton(page, 'Kindle', 'Send Test Email'));
    await page.settle();
    assert.deepEqual(messages(page), [{ text: 'Test email queued.', type: 'success' }]);
    assert.equal(callsTo(page, 'POST', '/api/email/devices/1/test').length, 1);

    page.route('POST /api/email/devices/1/test', respond(404, { error: 'Device not found' }));
    page.click(cardButton(page, 'Kindle', 'Send Test Email'));
    await page.settle();
    page.route('POST /api/email/devices/1/test', () => { throw new TypeError('Failed to fetch'); });
    page.click(cardButton(page, 'Kindle', 'Send Test Email'));
    await page.settle();

    assert.deepEqual(messageTexts(page, 'error'), [
        'Failed to send test email: Device not found',
        'Failed to send test email: Failed to fetch'
    ]);
});

test('read-only users see the devices with the write actions marked', async t => {
    const page = await bootPage({
        user: { canModifyLibrary: false, canAdminister: false },
        routes: { 'GET /api/email/devices': { devices: [KINDLE] } }
    });
    t.after(() => page.close());

    openFromSidebar(page);
    await page.settle();

    assert.ok(page.document.body.classList.contains('user-read-only'));
    assert.deepEqual(deviceCards(page).map(card => card.name), ['Kindle']);
    const writeOnly = label => cardButton(page, 'Kindle', label).hasAttribute('data-requires-write');
    assert.deepEqual([writeOnly('Edit'), writeOnly('Send Test Email'), writeOnly('Delete')], [false, true, true]);
    assert.ok(page.$('#emailDevicesModal .modal-footer .btn-primary').hasAttribute('data-requires-write'));
});

test('Delivery History opens on top of the devices modal; Escape and Close unwind them', async t => {
    const page = await bootPage({
        routes: {
            'GET /api/email/devices': { devices: [] },
            'GET /api/email/deliveries': { deliveries: [] }
        }
    });
    t.after(() => page.close());
    page.click('.settings-dropdown-item[onclick^="openEmailDevicesModal()"]');
    await page.settle();
    assert.ok(isModalOpen(page, 'emailDevicesModal'));

    page.click(buttonByText(page, '#emailDevicesModal .modal-footer', 'Delivery History'));
    await page.settle();
    assert.ok(isModalOpen(page, 'emailDeliveryHistoryModal'));
    assert.deepEqual(callsTo(page, 'GET', '/api/email/deliveries').map(call => call.query), [{ limit: '50' }]);

    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'emailDeliveryHistoryModal'));
    assert.ok(isModalOpen(page, 'emailDevicesModal'));

    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'emailDevicesModal'));

    await page.run('openEmailDevicesModal()');
    page.click(buttonByText(page, '#emailDevicesModal .modal-footer', 'Close'));
    assert.ok(!isModalOpen(page, 'emailDevicesModal'));
});

test('the settings page shows the email delivery status and refreshes it', async t => {
    const page = await bootPage({
        hash: '#/settings',
        routes: {
            'GET /api/settings': {},
            'GET /api/email/status': emailStatus()
        }
    });
    t.after(() => page.close());
    await page.waitFor(() => callsTo(page, 'GET', '/api/email/status').length === 1);
    await page.settle();
    const summary = () => page.$('#emailStatusSummary').textContent;

    assert.equal(summary(), '✅ Email delivery is configured · From: comics@example.com · Max: 25 MB');

    const refresh = buttonByText(page, page.$('#emailStatusSummary').parentElement, 'Refresh Status');
    page.route('GET /api/email/status', emailStatus({ configured: false, from_address: '', max_attachment_mb: 0 }));
    page.click(refresh);
    await page.settle();
    assert.equal(summary(), '⚠️ Email delivery is not fully configured');

    page.route('GET /api/email/status', respond(500, { error: 'boom' }));
    page.click(refresh);
    await page.settle();
    assert.equal(summary(), 'Unable to load email status.');
    assert.ok(page.consoleMessages.some(entry => entry.type === 'error' && entry.args[0] === 'Failed to load email status:'));
    assert.equal(callsTo(page, 'GET', '/api/email/status').length, 3, 'every refresh asks the server again');

    page.route('GET /api/email/devices', { devices: [] });
    page.click(buttonByText(page, page.$('#emailStatusSummary').parentElement, 'Manage Ereader Devices'));
    await page.settle();
    assert.ok(isModalOpen(page, 'emailDevicesModal'));
});
