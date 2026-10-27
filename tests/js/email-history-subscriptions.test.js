'use strict';

// js/app/email.js: the Delivery History modal and the per-series Auto-send
// (subscription) modal — loading states, rendering, saving, deleting and the
// failures of each request.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const {
    emailDevice,
    emailDelivery,
    emailSubscription,
    seriesSummary,
    seriesIssue,
    bootPage,
    bootSeriesPage,
    messages,
    isModalOpen,
    pressEscape,
    clickSeriesAction,
    buttonByText,
    callsTo
} = require('./support/email-helpers');

const KINDLE = emailDevice({ id: 1, name: 'Kindle', emailAddress: 'reader@kindle.com' });
const KOBO = emailDevice({ id: 2, name: 'Kobo', emailAddress: 'me@kobo.com', deliveryFormat: 'epub' });

const squash = text => text.replace(/\s+/g, ' ').trim();

function localDate(page, value) {
    return page.evaluate(`new Date(${JSON.stringify(value)}).toLocaleString()`);
}

/** Rows of a history or subscription list: the text of each line. */
function listRows(page, selector) {
    return page.$$(`${selector} .combine-folder-row`).map(row =>
        [...row.children].filter(child => child.tagName !== 'BUTTON').map(child => squash(child.textContent)));
}

function emptyText(page, selector) {
    const empty = page.$(`${selector} .empty-state, ${selector} .loading`);
    return empty ? squash(empty.textContent) : null;
}

test('Delivery History lists recent deliveries with status, device, format and errors', async t => {
    const deliveries = deferred();
    const page = await bootPage({ routes: { 'GET /api/email/deliveries': () => deliveries.promise } });
    t.after(() => page.close());

    page.click('.sidebar-link[onclick="openEmailDeliveryHistoryModal()"]');
    await page.settle();
    assert.ok(isModalOpen(page, 'emailDeliveryHistoryModal'));
    assert.equal(emptyText(page, '#emailDeliveryHistoryList'), 'Loading deliveries...');
    assert.deepEqual(callsTo(page, 'GET', '/api/email/deliveries').map(call => call.query), [{ limit: '50' }]);

    deliveries.resolve({
        deliveries: [
            emailDelivery(),
            emailDelivery({
                id: 2,
                fileName: 'Saga 002.cbz',
                status: 'failed',
                deliveryFormat: 'azw3',
                sentAt: null,
                errorMessage: 'Attachment exceeds the 25 MB limit'
            }),
            emailDelivery({ id: 3, fileName: null, filePath: 'Saga/Saga 003.cbz', status: 'pending', deliveryFormat: 'original', sentAt: null }),
            emailDelivery({ id: 4, fileName: 'Saga #1-5.epub', deviceName: 'Kobo', deviceEmail: 'me@kobo.com', issueCount: 5 }),
            emailDelivery({ id: 5, status: 'bounced', deliveryFormat: 'device', sentAt: 'not a date' }),
            emailDelivery({ id: 6, status: null, deliveryFormat: null, sentAt: null, createdAt: null })
        ]
    });
    await page.settle();

    const sent = localDate(page, '2024-03-01T10:01:00Z');
    const created = localDate(page, '2024-03-01T10:00:00Z');
    assert.deepEqual(listRows(page, '#emailDeliveryHistoryList'), [
        ['Saga 001.cbz', squash(`✅ Sent · Kindle (reader@kindle.com) · EPUB · ${sent}`)],
        [
            'Saga 002.cbz',
            squash(`❌ Failed · Kindle (reader@kindle.com) · AZW3 (Kindle) · ${created}`),
            'Attachment exceeds the 25 MB limit'
        ],
        ['Saga/Saga 003.cbz', squash(`⏳ Pending · Kindle (reader@kindle.com) · Original archive · ${created}`)],
        ['Saga #1-5.epub', squash(`✅ Sent · Kobo (me@kobo.com) · EPUB · condensed 5 issues · ${sent}`)],
        ['Saga 001.cbz', 'bounced · Kindle (reader@kindle.com) · Device default · —'],
        ['Saga 001.cbz', 'Unknown · Kindle (reader@kindle.com) · Original archive · —']
    ]);
});

test('Delivery History shows an empty history and load failures, and Refresh reloads it', async t => {
    const page = await bootPage({ routes: { 'GET /api/email/deliveries': { deliveries: [] } } });
    t.after(() => page.close());

    page.click('.sidebar-link[onclick="openEmailDeliveryHistoryModal()"]');
    await page.settle();
    assert.equal(emptyText(page, '#emailDeliveryHistoryList'), 'No comics have been emailed yet.');

    const refresh = buttonByText(page, '#emailDeliveryHistoryModal .modal-footer', 'Refresh');
    page.route('GET /api/email/deliveries', respond(500, { error: 'Database is locked' }));
    page.click(refresh);
    await page.settle();
    assert.equal(emptyText(page, '#emailDeliveryHistoryList'), 'Failed to load delivery history: Database is locked');

    page.route('GET /api/email/deliveries', () => { throw new TypeError('Failed to fetch'); });
    page.click(refresh);
    await page.settle();
    assert.equal(emptyText(page, '#emailDeliveryHistoryList'), 'Failed to load delivery history: Failed to fetch');

    page.route('GET /api/email/deliveries', {});
    page.click(refresh);
    await page.settle();
    assert.equal(emptyText(page, '#emailDeliveryHistoryList'), 'No comics have been emailed yet.');
    assert.equal(callsTo(page, 'GET', '/api/email/deliveries').length, 4);

    page.click(buttonByText(page, '#emailDeliveryHistoryModal .modal-footer', 'Close'));
    assert.ok(!isModalOpen(page, 'emailDeliveryHistoryModal'));
    page.click('.sidebar-link[onclick="openEmailDeliveryHistoryModal()"]');
    await page.settle();
    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'emailDeliveryHistoryModal'));
    assert.deepEqual(messages(page), []);
});

async function openAutoSend(t, { series = seriesSummary(), routes = {} } = {}) {
    const page = await bootSeriesPage({
        series,
        issues: [seriesIssue()],
        routes: {
            'GET /api/email/devices': { devices: [KINDLE, KOBO] },
            'GET /api/email/subscriptions': { subscriptions: [] },
            ...routes
        }
    });
    t.after(() => page.close());
    clickSeriesAction(page, 'Auto-send New Issues');
    await page.settle();
    assert.ok(isModalOpen(page, 'emailSubscriptionModal'));
    return page;
}

function deviceOptions(page) {
    return page.$$('#emailSubscriptionDeviceSelect option').map(option => [option.value, option.textContent.trim()]);
}

test('Auto-send New Issues loads the devices, then the subscriptions of the series', async t => {
    const devices = deferred();
    const page = await openAutoSend(t, {
        series: seriesSummary({ title: 'Saga & Friends' }),
        routes: {
            'GET /api/email/devices': () => devices.promise,
            'GET /api/email/subscriptions': {
                subscriptions: [
                    emailSubscription({ seriesTitle: 'Saga & Friends', lastSentAt: '2024-03-02T08:00:00Z' }),
                    emailSubscription({
                        id: 8,
                        seriesTitle: 'Saga & Friends',
                        deviceId: 2,
                        deviceName: 'Kobo',
                        deviceEmail: 'me@kobo.com',
                        deliveryFormat: 'original',
                        enabled: false
                    })
                ]
            }
        }
    });

    assert.equal(page.$('#emailSubscriptionSeries').textContent, 'Series: Saga & Friends');
    assert.deepEqual(deviceOptions(page), [['', 'Loading devices...']]);
    assert.equal(emptyText(page, '#emailSubscriptionsList'), 'Loading auto-send subscriptions...');
    assert.equal(callsTo(page, 'GET', '/api/email/subscriptions').length, 0, 'the devices come first');

    devices.resolve({ devices: [KINDLE, KOBO] });
    await page.settle();

    assert.deepEqual(deviceOptions(page), [['1', 'Kindle (reader@kindle.com)'], ['2', 'Kobo (me@kobo.com)']]);
    assert.deepEqual(callsTo(page, 'GET', '/api/email/subscriptions').map(call => call.query), [{ series: 'Saga & Friends' }]);
    const lastSent = localDate(page, '2024-03-02T08:00:00Z');
    assert.deepEqual(listRows(page, '#emailSubscriptionsList'), [
        ['Kindle · EPUB · Enabled', squash(`reader@kindle.com · Last sent ${lastSent}`)],
        ['Kobo · Original archive · Disabled', 'me@kobo.com']
    ]);
    const deletes = page.$$('#emailSubscriptionsList button');
    assert.deepEqual(deletes.map(button => button.textContent.trim()), ['Delete', 'Delete']);
    assert.ok(deletes.every(button => button.hasAttribute('data-requires-write')), 'hidden from read-only users');
    assert.ok(buttonByText(page, '#emailSubscriptionModal .modal-footer', 'Save Auto-send').hasAttribute('data-requires-write'));
});

test('Auto-send shows an empty list and load failures', async t => {
    const page = await openAutoSend(t, { routes: { 'GET /api/email/subscriptions': {} } });
    assert.equal(emptyText(page, '#emailSubscriptionsList'), 'No auto-send subscriptions for this series.');
    page.click(buttonByText(page, '#emailSubscriptionModal .modal-footer', 'Close'));
    assert.ok(!isModalOpen(page, 'emailSubscriptionModal'));

    page.route('GET /api/email/subscriptions', respond(403, { message: 'Not allowed' }));
    clickSeriesAction(page, 'Auto-send New Issues');
    await page.settle();
    assert.equal(emptyText(page, '#emailSubscriptionsList'), 'Failed to load subscriptions: Not allowed');

    page.route('GET /api/email/devices', respond(500, 'Internal Server Error'));
    clickSeriesAction(page, 'Auto-send New Issues');
    await page.settle();
    assert.equal(emptyText(page, '#emailSubscriptionsList'), 'Failed to load subscriptions: HTTP error! status: 500');

    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'emailSubscriptionModal'));
    assert.deepEqual(messages(page), []);
});

test('Save Auto-send saves the chosen device, format and state, then reloads the list', async t => {
    const page = await openAutoSend(t, { routes: { 'PUT /api/email/subscriptions': emailSubscription() } });
    page.route('GET /api/email/subscriptions', { subscriptions: [emailSubscription({ deviceId: 2, deviceName: 'Kobo', deviceEmail: 'me@kobo.com', deliveryFormat: 'azw3', enabled: false })] });

    page.input('#emailSubscriptionDeviceSelect', '2');
    page.input('#emailSubscriptionFormatSelect', 'azw3');
    page.input('#emailSubscriptionEnabledCheckbox', false);
    page.click(buttonByText(page, '#emailSubscriptionModal .modal-footer', 'Save Auto-send'));
    await page.settle();

    assert.deepEqual(callsTo(page, 'PUT', '/api/email/subscriptions').map(call => call.body), [{
        seriesTitle: 'Saga',
        deviceId: '2',
        deliveryFormat: 'azw3',
        enabled: false
    }]);
    assert.deepEqual(messages(page), [{ text: 'Auto-send subscription saved.', type: 'success' }]);
    assert.deepEqual(callsTo(page, 'GET', '/api/email/subscriptions').map(call => call.query), [{ series: 'Saga' }, { series: 'Saga' }]);
    assert.deepEqual(listRows(page, '#emailSubscriptionsList'), [['Kobo · AZW3 (Kindle) · Disabled', 'me@kobo.com']]);
    assert.ok(isModalOpen(page, 'emailSubscriptionModal'), 'stays open for further changes');
});

test('Save Auto-send needs a device and reports a rejected save', async t => {
    const page = await openAutoSend(t, {
        routes: {
            'GET /api/email/devices': { devices: [] },
            'PUT /api/email/subscriptions': respond(404, { error: 'Device not found' })
        }
    });
    const save = buttonByText(page, '#emailSubscriptionModal .modal-footer', 'Save Auto-send');

    assert.deepEqual(deviceOptions(page), []);
    page.click(save);
    await page.settle();
    assert.deepEqual(messages(page), [{ text: 'Choose an ereader device for auto-send.', type: 'error' }]);
    assert.equal(callsTo(page, 'PUT', '/api/email/subscriptions').length, 0);

    page.route('GET /api/email/devices', { devices: [KINDLE] });
    page.click(buttonByText(page, '#emailSubscriptionModal .modal-footer', 'Close'));
    clickSeriesAction(page, 'Auto-send New Issues');
    await page.settle();
    page.click(save);
    await page.settle();

    assert.deepEqual(callsTo(page, 'PUT', '/api/email/subscriptions').map(call => call.body), [{
        seriesTitle: 'Saga',
        deviceId: '1',
        deliveryFormat: 'device',
        enabled: true
    }]);
    assert.deepEqual(messages(page).at(-1), { text: 'Failed to save auto-send subscription: Device not found', type: 'error' });
    assert.equal(callsTo(page, 'GET', '/api/email/subscriptions').length, 2, 'the list is not reloaded after a failed save');
});

test('Delete removes a subscription and reloads the list, or reports why it could not', async t => {
    const page = await openAutoSend(t, {
        routes: {
            'GET /api/email/subscriptions': { subscriptions: [emailSubscription(), emailSubscription({ id: 8, deviceName: 'Kobo', deviceEmail: 'me@kobo.com' })] },
            'DELETE /api/email/subscriptions/7': undefined,
            'DELETE /api/email/subscriptions/8': respond(404, { error: 'Subscription not found' })
        }
    });

    page.route('GET /api/email/subscriptions', { subscriptions: [emailSubscription({ id: 8, deviceName: 'Kobo', deviceEmail: 'me@kobo.com' })] });
    page.click(page.$$('#emailSubscriptionsList button')[0]);
    await page.settle();

    assert.equal(callsTo(page, 'DELETE', '/api/email/subscriptions/7').length, 1);
    assert.deepEqual(messages(page), [{ text: 'Auto-send subscription deleted.', type: 'success' }]);
    assert.deepEqual(callsTo(page, 'GET', '/api/email/subscriptions').at(-1).query, { series: 'Saga' });
    assert.deepEqual(listRows(page, '#emailSubscriptionsList'), [['Kobo · EPUB · Enabled', 'me@kobo.com']]);

    page.click(page.$$('#emailSubscriptionsList button')[0]);
    await page.settle();
    assert.equal(callsTo(page, 'DELETE', '/api/email/subscriptions/8').length, 1);
    assert.deepEqual(messages(page).at(-1), { text: 'Failed to delete auto-send subscription: Subscription not found', type: 'error' });
    assert.equal(listRows(page, '#emailSubscriptionsList').length, 1);
});
