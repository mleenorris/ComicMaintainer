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
    answerConfirms,
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
    assert.deepEqual(callsTo(page, 'GET', '/api/email/deliveries').map(call => call.query), [{ limit: '50', offset: '0' }]);

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

const openHistory = async page => {
    page.click('.sidebar-link[onclick="openEmailDeliveryHistoryModal()"]');
    await page.settle();
};
const refreshHistory = async page => {
    page.click(buttonByText(page, '#emailDeliveryHistoryModal .modal-footer', 'Refresh'));
    await page.settle();
};
const clearMessages = page => page.$('#messageContainer').replaceChildren();

/**
 * GET /api/email/deliveries over a history of `total()` deliveries, numbered
 * newest first, answering the page its limit/offset query asks for.
 */
function pagedHistory(total, overrides = {}) {
    return call => {
        const offset = Number(call.query.offset) || 0;
        const limit = Number(call.query.limit) || 50;
        const count = total();
        const deliveries = [];
        for (let id = count - offset; id > Math.max(0, count - offset - limit); id--) {
            deliveries.push(emailDelivery({ id, fileName: `Saga ${String(id).padStart(3, '0')}.cbz` }));
        }
        return { deliveries, total: count, offset, limit, pending_count: 0, rate_limited_until: null, ...overrides };
    };
}

test('Delivery History pages through the whole history, 50 deliveries at a time', async t => {
    const page = await bootPage({ routes: { 'GET /api/email/deliveries': pagedHistory(() => 120) } });
    t.after(() => page.close());
    const pageInfo = () => page.$('#emailDeliveryHistoryPageInfo').textContent;
    const rows = () => listRows(page, '#emailDeliveryHistoryList');
    const prev = page.$('#emailDeliveryHistoryPrevBtn');
    const next = page.$('#emailDeliveryHistoryNextBtn');

    await openHistory(page);
    assert.equal(page.$('#emailDeliveryHistoryPagination').hidden, false);
    assert.equal(pageInfo(), 'Page 1 of 3 (120 total)');
    assert.equal(rows().length, 50);
    assert.equal(rows()[0][0], 'Saga 120.cbz');
    assert.equal(prev.disabled, true);
    assert.equal(next.disabled, false);

    page.click(next);
    await page.settle();
    assert.equal(pageInfo(), 'Page 2 of 3 (120 total)');
    assert.deepEqual([rows()[0][0], rows().at(-1)[0]], ['Saga 070.cbz', 'Saga 021.cbz']);
    assert.equal(prev.disabled, false);
    assert.equal(next.disabled, false);

    page.click(next);
    await page.settle();
    assert.equal(pageInfo(), 'Page 3 of 3 (120 total)');
    assert.equal(rows().length, 20);
    assert.equal(next.disabled, true);
    await page.run('loadNextEmailDeliveryHistoryPage()');

    // Refresh stays on the page shown.
    await refreshHistory(page);
    assert.equal(pageInfo(), 'Page 3 of 3 (120 total)');

    page.click(prev);
    await page.settle();
    assert.equal(pageInfo(), 'Page 2 of 3 (120 total)');
    assert.deepEqual(
        callsTo(page, 'GET', '/api/email/deliveries').map(call => call.query),
        ['0', '50', '100', '100', '50'].map(offset => ({ limit: '50', offset })),
        'there is no page after the last one');

    // Reopening starts again from the newest deliveries.
    page.click(buttonByText(page, '#emailDeliveryHistoryModal .modal-footer', 'Close'));
    await openHistory(page);
    assert.equal(pageInfo(), 'Page 1 of 3 (120 total)');
    assert.equal(callsTo(page, 'GET', '/api/email/deliveries').at(-1).query.offset, '0');
    await page.run('loadPreviousEmailDeliveryHistoryPage()');
    assert.equal(callsTo(page, 'GET', '/api/email/deliveries').length, 6, 'there is no page before the first one');
    assert.deepEqual(messages(page), []);
});

test('the pager is hidden for one page, and a page emptied meanwhile falls back to the last page', async t => {
    let total = 60;
    const page = await bootPage({ routes: { 'GET /api/email/deliveries': pagedHistory(() => total) } });
    t.after(() => page.close());
    const pagination = page.$('#emailDeliveryHistoryPagination');

    await openHistory(page);
    assert.equal(pagination.hidden, false);
    page.click(page.$('#emailDeliveryHistoryNextBtn'));
    await page.settle();
    assert.equal(page.$('#emailDeliveryHistoryPageInfo').textContent, 'Page 2 of 2 (60 total)');

    total = 30;
    await refreshHistory(page);
    assert.equal(page.$('#emailDeliveryHistoryPageInfo').textContent, 'Page 1 of 1 (30 total)');
    assert.equal(pagination.hidden, true);
    assert.equal(listRows(page, '#emailDeliveryHistoryList').length, 30);
    assert.deepEqual(callsTo(page, 'GET', '/api/email/deliveries').map(call => call.query.offset), ['0', '50', '50', '0']);

    // An older server that reports no total still shows what it returned, without a pager.
    page.route('GET /api/email/deliveries', { deliveries: [emailDelivery()] });
    await refreshHistory(page);
    assert.equal(pagination.hidden, true);
    assert.equal(listRows(page, '#emailDeliveryHistoryList').length, 1);
});

test('only the latest Delivery History request is shown, and paging waits for it', async t => {
    const first = deferred();
    const responses = [
        () => first.promise,
        () => ({ deliveries: [emailDelivery({ id: 9, fileName: 'Newest.cbz' })], total: 51, pending_count: 0 })
    ];
    const page = await bootPage({ routes: { 'GET /api/email/deliveries': call => responses.shift()(call) } });
    t.after(() => page.close());

    await openHistory(page);
    assert.equal(page.$('#emailDeliveryHistoryPrevBtn').disabled, true);
    assert.equal(page.$('#emailDeliveryHistoryNextBtn').disabled, true, 'no paging while a page loads');
    await refreshHistory(page);
    assert.deepEqual(listRows(page, '#emailDeliveryHistoryList').map(row => row[0]), ['Newest.cbz']);
    assert.equal(page.$('#emailDeliveryHistoryNextBtn').disabled, false);

    first.resolve({ deliveries: [emailDelivery({ fileName: 'Stale.cbz' })], total: 1, pending_count: 3 });
    await page.settle();
    assert.deepEqual(listRows(page, '#emailDeliveryHistoryList').map(row => row[0]), ['Newest.cbz']);
    assert.equal(page.$('#emailDeliveryHistoryPageInfo').textContent, 'Page 1 of 2 (51 total)');
    assert.equal(page.$('#emailDeliveryCancelAllBtn').hidden, true);
});

test('Cancel stops a pending delivery and reloads the history', async t => {
    let deliveries = [
        emailDelivery({ id: 3, fileName: 'Saga 003.cbz', status: 'pending', sentAt: null }),
        emailDelivery({ id: 2, fileName: 'Saga 002.cbz', status: 'cancelled', sentAt: null }),
        emailDelivery({ id: 1 })
    ];
    const page = await bootPage({
        routes: {
            'GET /api/email/deliveries': () => ({
                deliveries,
                total: deliveries.length,
                pending_count: deliveries.filter(delivery => delivery.status === 'pending').length
            })
        }
    });
    t.after(() => page.close());

    await openHistory(page);
    const created = localDate(page, '2024-03-01T10:00:00Z');
    assert.deepEqual(listRows(page, '#emailDeliveryHistoryList'), [
        ['Saga 003.cbz', squash(`⏳ Pending · Kindle (reader@kindle.com) · EPUB · ${created}`)],
        ['Saga 002.cbz', squash(`🚫 Cancelled · Kindle (reader@kindle.com) · EPUB · ${created}`)],
        ['Saga 001.cbz', squash(`✅ Sent · Kindle (reader@kindle.com) · EPUB · ${localDate(page, '2024-03-01T10:01:00Z')}`)]
    ]);
    const buttons = page.$$('#emailDeliveryHistoryList button');
    assert.deepEqual(buttons.map(button => button.textContent.trim()), ['Cancel'], 'only a pending delivery can be cancelled');
    assert.match(buttons[0].closest('.combine-folder-row').textContent, /Saga 003\.cbz/);
    assert.ok(buttons[0].hasAttribute('data-requires-write'), 'hidden from read-only users');

    const cancel = deferred();
    page.route('POST /api/email/deliveries/3/cancel', () => cancel.promise);
    page.click(buttons[0]);
    await page.settle();
    assert.equal(buttons[0].disabled, true, 'disabled while the request runs');

    deliveries = [{ ...deliveries[0], status: 'cancelled' }, ...deliveries.slice(1)];
    cancel.resolve({ cancelled: true, delivery: deliveries[0] });
    await page.settle();
    assert.deepEqual(messages(page), [{ text: 'Delivery cancelled.', type: 'success' }]);
    assert.equal(listRows(page, '#emailDeliveryHistoryList')[0][1].startsWith('🚫 Cancelled'), true);
    assert.equal(page.$$('#emailDeliveryHistoryList button').length, 0);
    const posts = callsTo(page, 'POST', '/api/email/deliveries/3/cancel');
    assert.equal(posts.length, 1);
    assert.ok(posts[0].headers.authorization, 'the request is authenticated');
    assert.equal(callsTo(page, 'GET', '/api/email/deliveries').length, 2);
});

test('Cancel reports a delivery that is still stopping, already being sent or gone', async t => {
    const pending = emailDelivery({ id: 4, status: 'pending', sentAt: null });
    const page = await bootPage({
        routes: { 'GET /api/email/deliveries': { deliveries: [pending], total: 1, pending_count: 1 } }
    });
    t.after(() => page.close());
    await openHistory(page);

    const cases = [
        [respond(202, { cancelled: false, stopping: true, delivery: pending }),
            { type: 'info', text: 'Stopping the delivery. It will be cancelled as soon as the current step finishes.' }],
        [respond(409, { error: 'The delivery is already being sent and can no longer be cancelled.', delivery: pending }),
            { type: 'error', text: 'Failed to cancel the delivery: The delivery is already being sent and can no longer be cancelled.' }],
        [respond(409, { error: 'The delivery has already been sent.' }),
            { type: 'error', text: 'Failed to cancel the delivery: The delivery has already been sent.' }],
        [respond(404, { error: 'Delivery not found' }), { type: 'error', text: 'Failed to cancel the delivery: Delivery not found' }],
        [() => { throw new TypeError('Failed to fetch'); }, { type: 'error', text: 'Failed to cancel the delivery: Failed to fetch' }]
    ];
    for (const [response, expected] of cases) {
        page.route('POST /api/email/deliveries/4/cancel', response);
        clearMessages(page);
        page.click(buttonByText(page, '#emailDeliveryHistoryList', 'Cancel'));
        await page.settle();
        assert.deepEqual(messages(page), [{ text: expected.text, type: expected.type }]);
    }
    assert.equal(callsTo(page, 'POST', '/api/email/deliveries/4/cancel').length, cases.length);
    assert.equal(callsTo(page, 'GET', '/api/email/deliveries').length, cases.length + 1, 'every attempt reloads the history');
});

test('Cancel All Pending cancels the pending deliveries of every page after confirming', async t => {
    let pendingCount = 3;
    const page = await bootPage({
        routes: { 'GET /api/email/deliveries': pagedHistory(() => 120, { get pending_count() { return pendingCount; } }) }
    });
    t.after(() => page.close());
    await openHistory(page);
    const cancelAll = page.$('#emailDeliveryCancelAllBtn');
    assert.equal(cancelAll.hidden, false);
    assert.equal(cancelAll.textContent, 'Cancel All Pending (3)');
    assert.ok(cancelAll.hasAttribute('data-requires-write'), 'hidden from read-only users');

    answerConfirms(page, false, true);
    page.click(cancelAll);
    await page.settle();
    assert.deepEqual(page.dialogs.map(dialog => dialog.message),
        ['Cancel all 3 pending deliveries? A delivery already being handed to the mail server is still sent.']);
    assert.equal(callsTo(page, 'POST', '/api/email/deliveries/cancel-pending').length, 0, 'declining cancels nothing');

    const request = deferred();
    page.route('POST /api/email/deliveries/cancel-pending', () => request.promise);
    page.click(cancelAll);
    await page.settle();
    assert.equal(cancelAll.disabled, true, 'disabled while the request runs');

    pendingCount = 0;
    request.resolve({ cancelled: 2, stopping: 1, sending: 0 });
    await page.settle();
    assert.deepEqual(messages(page), [{ text: 'Cancelled 2 pending deliveries. 1 delivery is still stopping.', type: 'success' }]);
    assert.equal(cancelAll.hidden, true, 'nothing is left to cancel');
    assert.equal(callsTo(page, 'POST', '/api/email/deliveries/cancel-pending').length, 1);
    assert.equal(callsTo(page, 'GET', '/api/email/deliveries').length, 2);
});

test('Cancel All Pending says what could not be cancelled, and reports failures', async t => {
    const page = await bootPage({
        routes: {
            'GET /api/email/deliveries': { deliveries: [emailDelivery({ status: 'pending', sentAt: null })], total: 1, pending_count: 1 }
        }
    });
    t.after(() => page.close());
    await openHistory(page);
    const cancelAll = page.$('#emailDeliveryCancelAllBtn');
    assert.equal(cancelAll.textContent, 'Cancel All Pending (1)');
    answerConfirms(page, true);

    const cases = [
        [{ cancelled: 1, stopping: 0, sending: 0 }, { type: 'success', text: 'Cancelled 1 pending delivery.' }],
        [{ cancelled: 0, stopping: 2, sending: 1 }, { type: 'info', text: '2 deliveries are still stopping. 1 delivery was already being sent.' }],
        [{ cancelled: 1, stopping: 0, sending: 3 }, { type: 'success', text: 'Cancelled 1 pending delivery. 3 deliveries were already being sent.' }],
        [{ cancelled: 0, stopping: 0, sending: 0 }, { type: 'info', text: 'There were no pending deliveries to cancel.' }],
        [respond(500, { error: 'Database is locked' }), { type: 'error', text: 'Failed to cancel pending deliveries: Database is locked' }]
    ];
    for (const [response, expected] of cases) {
        page.route('POST /api/email/deliveries/cancel-pending', response);
        clearMessages(page);
        page.click(cancelAll);
        await page.settle();
        assert.deepEqual(messages(page), [{ text: expected.text, type: expected.type }]);
        assert.equal(cancelAll.disabled, false, 'enabled again once the history has reloaded');
    }
    assert.equal(page.dialogs[0].message,
        'Cancel the pending delivery? A delivery already being handed to the mail server is still sent.');

    // A failed reload keeps the button usable.
    page.route('GET /api/email/deliveries', respond(500, { error: 'Database is locked' }));
    page.route('POST /api/email/deliveries/cancel-pending', { cancelled: 1, stopping: 0, sending: 0 });
    page.click(cancelAll);
    await page.settle();
    assert.equal(emptyText(page, '#emailDeliveryHistoryList'), 'Failed to load delivery history: Database is locked');
    assert.equal(cancelAll.disabled, false);
});

test('Delivery History says until when the email rate limit holds deliveries back', async t => {
    const until = '2024-03-01T11:00:00Z';
    const page = await bootPage({
        routes: {
            'GET /api/email/deliveries': {
                deliveries: [emailDelivery({ status: 'pending', sentAt: null })],
                total: 1,
                pending_count: 1,
                rate_limited_until: until
            }
        }
    });
    t.after(() => page.close());
    const notice = page.$('#emailDeliveryHistoryRateLimit');

    await openHistory(page);
    assert.equal(notice.hidden, false);
    assert.equal(notice.textContent, `⏸ The email rate limit is holding pending deliveries back until ${localDate(page, until)}.`);

    for (const rateLimitedUntil of [null, undefined, 'not a date']) {
        page.route('GET /api/email/deliveries', { deliveries: [], total: 0, pending_count: 0, rate_limited_until: rateLimitedUntil });
        await refreshHistory(page);
        assert.equal(notice.hidden, true);
        assert.equal(notice.textContent, '');
    }

    // Reopened, the notice from the last visit is gone until the server repeats it.
    page.route('GET /api/email/deliveries', { deliveries: [], total: 0, pending_count: 0, rate_limited_until: until });
    await refreshHistory(page);
    assert.equal(notice.hidden, false);
    page.click(buttonByText(page, '#emailDeliveryHistoryModal .modal-footer', 'Close'));
    const reload = deferred();
    page.route('GET /api/email/deliveries', () => reload.promise);
    await openHistory(page);
    assert.equal(notice.hidden, true);
    reload.resolve({ deliveries: [], total: 0, pending_count: 0, rate_limited_until: null });
    await page.settle();
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
