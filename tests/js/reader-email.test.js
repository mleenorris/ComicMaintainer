'use strict';

// Emailing the open comic to an ereader from the reader (js/pages/reader.js;
// EmailController: /api/email/status, /api/email/devices, /api/email/send).

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { FILE, openReader, loadReader, press, toasts, shownPage } = require('./support/reader-helpers');

const DEVICES = {
    devices: [
        { id: 3, name: 'Kindle', emailAddress: 'me@kindle.com' },
        { id: 7, name: 'Kobo', emailAddress: 'me@kobo.com' }
    ]
};
const STATUS = { configured: true, from_address: 'comics@example.com', max_attachment_mb: 25 };

const prompt = page => page.$('#emailPrompt');
const sendButton = page => page.$('#emailPromptSendBtn');
const deviceOptions = page => page.$$('#emailPromptDevice option').map(option => [option.textContent, option.value]);
const sends = page => page.fetchCalls.filter(call => call.method === 'POST' && call.path === '/api/email/send');

function openWithEmail(routes = {}) {
    return openReader({
        routes: {
            'GET /api/email/status': STATUS,
            'GET /api/email/devices': DEVICES,
            'POST /api/email/send': { queued: [{ filePath: FILE }], skipped: [] },
            ...routes
        }
    });
}

/** Click the header's Email button and wait for the device list to settle. */
async function openPrompt(page) {
    page.click('#emailComicBtn');
    await page.waitFor(() => page.fetchCalls.some(call => call.path === '/api/email/devices'));
    await page.settle();
}

/** Click Send and wait for the outcome toast. */
async function send(page) {
    const before = toasts(page).length;
    page.click(sendButton(page));
    await page.waitFor(() => toasts(page).length > before);
    return toasts(page).at(-1);
}

test('the email button lists the ereader devices and sends the comic to the chosen one', async () => {
    const page = await openWithEmail();

    page.click('#emailComicBtn');
    assert.ok(prompt(page).classList.contains('show'));
    assert.deepEqual(deviceOptions(page), [['Loading devices…', '']]);
    assert.equal(sendButton(page).disabled, true);

    await page.waitFor(() => !sendButton(page).disabled);
    assert.deepEqual(deviceOptions(page), [['Kindle (me@kindle.com)', '3'], ['Kobo (me@kobo.com)', '7']]);
    for (const call of page.fetchCalls.filter(call => call.path.startsWith('/api/email/'))) {
        assert.equal(call.headers['content-type'], 'application/json');
        assert.match(call.headers.authorization, /^Bearer /);
    }

    page.$('#emailPromptDevice').value = '7';
    page.$('#emailPromptFormat').value = 'epub';
    page.click(sendButton(page));
    // No double sends while the request is out.
    assert.equal(sendButton(page).disabled, true);
    await page.waitFor(() => toasts(page).length === 1);

    assert.deepEqual(sends(page).map(call => call.body), [{ files: [FILE], deviceId: 7, deliveryFormat: 'epub', skipAlreadyDelivered: false }]);
    assert.deepEqual(toasts(page), [{ message: 'Comic queued for delivery.', type: 'success' }]);
    assert.equal(prompt(page).classList.contains('show'), false);
    assert.equal(sendButton(page).disabled, false);
});

test("the device's default format is left to the server, and an empty result says nothing was queued", async () => {
    const page = await openWithEmail({ 'POST /api/email/send': { queued: [], skipped: [{ filePath: FILE, reason: 'Already delivered' }] } });
    await openPrompt(page);

    assert.equal(page.$('#emailPromptFormat').value, 'device');
    assert.deepEqual(await send(page), { message: 'Nothing was queued.', type: 'info' });
    assert.deepEqual(sends(page)[0].body, { files: [FILE], deviceId: 3, skipAlreadyDelivered: false });
});

test('replies without a body count as nothing queued', async () => {
    for (const reply of [respond(204), () => new Response('OK', { status: 200 })]) {
        const page = await openWithEmail({ 'POST /api/email/send': reply });
        await openPrompt(page);
        assert.deepEqual(await send(page), { message: 'Nothing was queued.', type: 'info' });
    }
});

test('a send the server refuses is reported and the prompt stays open to try again', async () => {
    const cases = [
        [respond(400, { error: 'At least one file is required' }), 'Failed to email comic: At least one file is required'],
        [respond(503, { message: 'SMTP server unavailable' }), 'Failed to email comic: SMTP server unavailable'],
        [() => new Response('<html>Bad gateway</html>', { status: 502 }), 'Failed to email comic: HTTP error! status: 502'],
        [() => { throw new TypeError('Failed to fetch'); }, 'Failed to email comic: Failed to fetch']
    ];
    for (const [reply, message] of cases) {
        const page = await openWithEmail({ 'POST /api/email/send': reply });
        await openPrompt(page);
        assert.deepEqual(await send(page), { message, type: 'error' });
        assert.ok(prompt(page).classList.contains('show'));
        assert.equal(sendButton(page).disabled, false);
    }
});

test('with no saved devices the prompt asks for one to be added first', async () => {
    for (const devices of [{ devices: [] }, {}]) {
        const page = await openWithEmail({ 'GET /api/email/devices': devices });
        await openPrompt(page);

        assert.deepEqual(deviceOptions(page), [['No devices saved', '']]);
        assert.equal(sendButton(page).disabled, true);
        assert.deepEqual(toasts(page), [{ message: 'Add an ereader device from the library menu first.', type: 'error' }]);
    }
});

test('when email delivery is not configured the devices are listed but cannot be sent to', async () => {
    const page = await openWithEmail({ 'GET /api/email/status': { ...STATUS, configured: false } });
    await openPrompt(page);

    assert.equal(deviceOptions(page).length, 2);
    assert.equal(sendButton(page).disabled, true);
    assert.deepEqual(toasts(page), [{ message: 'Email delivery is not configured. Ask an administrator to set the SMTP settings.', type: 'error' }]);
});

test('a status check that fails does not stop the comic being sent', async () => {
    const page = await openWithEmail({ 'GET /api/email/status': respond(500, { error: 'boom' }) });
    await openPrompt(page);

    assert.equal(sendButton(page).disabled, false);
    assert.deepEqual(toasts(page), []);
});

test("a device list that cannot be loaded is reported with the server's reason", async () => {
    const cases = [
        [respond(500, { error: 'Database is locked' }), 'Failed to load ereader devices: Database is locked'],
        [() => new Response('nope', { status: 403 }), 'Failed to load ereader devices: HTTP error! status: 403']
    ];
    for (const [reply, message] of cases) {
        const page = await openWithEmail({ 'GET /api/email/devices': reply });
        await openPrompt(page);

        assert.deepEqual(deviceOptions(page), [['Failed to load devices', '']]);
        assert.equal(sendButton(page).disabled, true);
        assert.deepEqual(toasts(page), [{ message, type: 'error' }]);
    }
});

test('Send refuses to go without a device', async () => {
    const page = await openWithEmail({ 'GET /api/email/devices': { devices: [] } });
    await openPrompt(page);

    // The button is disabled; its handler double-checks all the same.
    await page.run('submitReaderEmail()');
    assert.equal(toasts(page).at(-1).message, 'Choose an ereader device.');
    assert.deepEqual(sends(page), []);
});

test('Escape and Cancel close the prompt without leaving the reader, and other keys wait', async () => {
    const page = await openWithEmail();
    await openPrompt(page);

    assert.equal(press(page, 'ArrowRight').defaultPrevented, false);
    await page.settle();
    assert.equal(shownPage(page).alt, 'Page 1');

    assert.equal(press(page, 'Escape').defaultPrevented, true);
    assert.equal(prompt(page).classList.contains('show'), false);
    assert.deepEqual(page.navigations, []);

    await openPrompt(page);
    page.click('#emailPrompt button:not(#emailPromptSendBtn)');
    assert.equal(prompt(page).classList.contains('show'), false);
    assert.deepEqual(sends(page), []);
});

test('the email button explains when no comic is open', async () => {
    const page = loadReader({ file: null });
    await page.settle();

    page.click('#emailComicBtn');
    assert.deepEqual(toasts(page), [{ message: 'No comic is open.', type: 'error' }]);
    assert.equal(prompt(page).classList.contains('show'), false);
});

test('without the prompt markup the email button does nothing', async () => {
    const page = await openReader({ beforeScripts: window => window.document.getElementById('emailPrompt').remove() });

    page.click('#emailComicBtn');
    await page.settle();
    assert.equal(page.fetchCalls.some(call => call.path.startsWith('/api/email/')), false);
    assert.deepEqual(toasts(page), []);
});
