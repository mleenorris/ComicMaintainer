'use strict';

// js/app/email.js: the Email to Ereader dialog — opening it for one issue, a
// selection or a whole series, the device/status checks that gate Send, and
// sending (request bodies, queued/skipped read-out, errors).

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const {
    emailDevice,
    emailStatus,
    seriesSummary,
    seriesIssue,
    bootPage,
    bootSeriesPage,
    messages,
    messageTexts,
    isModalOpen,
    pressEscape,
    clickIssueAction,
    clickSeriesAction,
    buttonByText,
    callsTo
} = require('./support/email-helpers');

const FIRST = 'Saga/Saga 001.cbz';
const SECOND = 'Saga/Saga 002.cbz';
const ISSUES = [
    seriesIssue(),
    seriesIssue({ file_path: SECOND, file_name: 'Saga 002.cbz', title: 'Saga 002', issue: '2' })
];
const KINDLE = emailDevice({ id: 1, name: 'Kindle', emailAddress: 'reader@kindle.com' });
const KOBO = emailDevice({ id: 2, name: 'Kobo', emailAddress: 'me@kobo.com', deliveryFormat: 'epub' });

const EMAIL_ROUTES = {
    'GET /api/email/status': emailStatus(),
    'GET /api/email/devices': { devices: [KINDLE, KOBO] },
    'GET /api/email/condense-builds': { builds: [] }
};

function boot({ routes = {}, ...options } = {}) {
    return bootSeriesPage({ issues: ISSUES, routes: { ...EMAIL_ROUTES, ...routes }, ...options });
}

// ComicEmailDeliveryDto entries of a send response.
function queued(...paths) {
    return paths.map((filePath, index) => ({ id: index + 1, filePath, status: 'pending' }));
}

function sendState(page) {
    return {
        open: isModalOpen(page, 'emailSendModal'),
        summary: page.$('#emailSendSummary').textContent,
        options: page.$$('#emailSendDeviceSelect option').map(option => [option.value, option.textContent.trim()]),
        hint: page.$('#emailSendStatusHint').hidden ? null : page.$('#emailSendStatusHint').textContent,
        noDevices: !page.$('#emailSendNoDevices').hidden,
        deviceGroup: !page.$('#emailSendDeviceGroup').hidden,
        sendDisabled: page.$('#emailSendSubmitBtn').disabled
    };
}

test('Email to Ereader… on an issue opens the dialog and loads the devices', async t => {
    const status = deferred();
    const devices = deferred();
    const page = await boot({
        routes: { 'GET /api/email/status': () => status.promise, 'GET /api/email/devices': () => devices.promise }
    });
    t.after(() => page.close());

    clickIssueAction(page, FIRST, 'Email to Ereader…');
    await page.settle();

    assert.deepEqual(sendState(page), {
        open: true,
        summary: 'Send 1 selected file.',
        options: [['', 'Loading devices...']],
        hint: null,
        noDevices: false,
        deviceGroup: true,
        sendDisabled: false
    });
    assert.equal(callsTo(page, 'GET', '/api/email/condense-builds').length, 1, 'running builds are shown straight away');

    status.resolve(emailStatus());
    devices.resolve({ devices: [KINDLE, KOBO] });
    await page.settle();

    assert.deepEqual(sendState(page), {
        open: true,
        summary: 'Send 1 selected file.',
        options: [['1', 'Kindle (reader@kindle.com)'], ['2', 'Kobo (me@kobo.com)']],
        hint: null,
        noDevices: false,
        deviceGroup: true,
        sendDisabled: false
    });
    assert.equal(page.$('#emailCondenseModeSelect').value, 'none');
    assert.ok(page.$('#emailCondensePlan').hidden);
});

test('Send for one issue posts the file with the chosen device and format', async t => {
    const page = await boot({ routes: { 'POST /api/email/send': { queued: queued(FIRST), skipped: {} } } });
    t.after(() => page.close());
    clickIssueAction(page, FIRST, 'Email to Ereader…');
    await page.settle();

    page.input('#emailSendDeviceSelect', '2');
    page.input('#emailSendFormatSelect', 'azw3');
    page.input('#emailSkipAlreadyDeliveredCheckbox', false);
    page.click('#emailSendSubmitBtn');
    await page.settle();

    const [call] = callsTo(page, 'POST', '/api/email/send');
    assert.deepEqual(call.body, {
        files: [FIRST],
        deviceId: '2',
        deliveryFormat: 'azw3',
        skipAlreadyDelivered: false,
        condenseMode: 'none',
        issuesPerBook: 5
    });
    assert.equal(call.headers['content-type'], 'application/json');
    assert.ok(!isModalOpen(page, 'emailSendModal'));
    assert.ok(!page.$('#emailSendSubmitBtn').disabled);
    assert.deepEqual(messages(page), [{ text: 'Queued 1 email delivery.', type: 'success' }]);
    assert.equal(callsTo(page, 'GET', '/api/email/status').length, 1);
});

test('Email Selected sends the checked issues and lists what the server skipped', async t => {
    const page = await boot({
        routes: {
            'POST /api/email/send': { queued: queued(FIRST), skipped: { [SECOND]: 'Already sent to this device' } }
        }
    });
    t.after(() => page.close());
    assert.ok(page.$('#emailSelectedItem').disabled);
    for (const box of page.$$('.series-issue-card input[type="checkbox"]')) page.input(box, true);
    assert.ok(!page.$('#emailSelectedItem').disabled);

    page.click('#emailSelectedItem');
    await page.settle();
    assert.equal(page.$('#emailSendSummary').textContent, 'Send 2 selected files.');

    page.click('#emailSendSubmitBtn');
    await page.settle();

    const [call] = callsTo(page, 'POST', '/api/email/send');
    // "Device default" leaves the format to the device, so none is sent.
    assert.deepEqual(call.body, {
        files: [FIRST, SECOND],
        deviceId: '1',
        skipAlreadyDelivered: true,
        condenseMode: 'none',
        issuesPerBook: 5
    });
    assert.deepEqual(messages(page), [
        { text: 'Queued 1 email delivery; skipped 1.', type: 'success' },
        { text: 'Skipped: Saga 002.cbz: Already sent to this device', type: 'warning' }
    ]);
});

test('only the first five skipped files are listed', async t => {
    const skipped = Object.fromEntries(Array.from({ length: 7 }, (_, index) => [`Saga/Saga 00${index + 1}.cbz`, 'Already sent']));
    const page = await boot({ routes: { 'POST /api/email/send': { queued: [], skipped } } });
    t.after(() => page.close());

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);
    await page.run('submitEmailSend()');

    assert.deepEqual(messages(page), [
        { text: 'Queued 0 email deliveries; skipped 7.', type: 'info' },
        {
            text: 'Skipped: Saga 001.cbz: Already sent; Saga 002.cbz: Already sent; Saga 003.cbz: Already sent; '
                + 'Saga 004.cbz: Already sent; Saga 005.cbz: Already sent; …',
            type: 'warning'
        }
    ]);

    page.route('POST /api/email/send', {});
    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);
    await page.run('submitEmailSend()');
    assert.deepEqual(messages(page)[2], { text: 'Queued 0 email deliveries.', type: 'info' });
});

test('Email All Issues sends the series by id', async t => {
    const page = await boot({
        routes: { 'POST /api/email/send-series': { queued: queued(FIRST, SECOND), skipped: {} } }
    });
    t.after(() => page.close());

    clickSeriesAction(page, 'Email All Issues');
    await page.settle();
    assert.equal(page.$('#emailSendSummary').textContent, 'Send all issues in "Saga".');

    page.input('#emailSendFormatSelect', 'device');
    page.click('#emailSendSubmitBtn');
    await page.settle();

    const [call] = callsTo(page, 'POST', '/api/email/send-series');
    // Unlike the file send, the series send always names the format.
    assert.deepEqual(call.body, {
        seriesId: 'saga',
        deviceId: '1',
        deliveryFormat: 'device',
        skipAlreadyDelivered: true,
        condenseMode: 'none',
        issuesPerBook: 5
    });
    assert.deepEqual(messages(page), [{ text: 'Queued 2 email deliveries.', type: 'success' }]);
    assert.ok(!isModalOpen(page, 'emailSendModal'));
});

test('a series without a title is described generically', async t => {
    const page = await boot();
    t.after(() => page.close());

    await page.run("openEmailSendModalForSeries('saga', '')");

    assert.equal(page.$('#emailSendSummary').textContent, 'Send all issues in "this series".');
});

test('send failures keep the dialog open and re-enable Send', async t => {
    const response = deferred();
    const page = await boot({ routes: { 'POST /api/email/send-series': () => response.promise } });
    t.after(() => page.close());
    await page.run("openEmailSendModalForSeries('saga', 'Saga')");

    page.click('#emailSendSubmitBtn');
    await page.settle();
    assert.ok(page.$('#emailSendSubmitBtn').disabled, 'Send is disabled while the request runs');

    const tooLarge = 'Condensed book "Saga #1-12" is about 41.2 MB which exceeds the 25 MB attachment limit. '
        + 'Download it instead, or condense fewer issues per book.';
    response.resolve(respond(400, { error: tooLarge }));
    await page.settle();
    assert.deepEqual(messageTexts(page, 'error'), [`Failed to email comics: ${tooLarge}`]);
    assert.ok(isModalOpen(page, 'emailSendModal'));
    assert.ok(!page.$('#emailSendSubmitBtn').disabled);

    page.route('POST /api/email/send-series', respond(404, { error: 'Series not found' }));
    await page.run('submitEmailSend()');
    page.route('POST /api/email/send-series', () => { throw new TypeError('Failed to fetch'); });
    await page.run('submitEmailSend()');

    assert.deepEqual(messageTexts(page, 'error').slice(1), [
        'Failed to email comics: Series not found',
        'Failed to email comics: Failed to fetch'
    ]);
    assert.ok(!page.$('#emailSendSubmitBtn').disabled);
});

test('without saved devices the dialog offers to add one', async t => {
    const page = await boot({ routes: { 'GET /api/email/devices': { devices: [] } } });
    t.after(() => page.close());

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);

    assert.deepEqual(sendState(page), {
        open: true,
        summary: 'Send 1 selected file.',
        options: [['', 'No devices saved']],
        hint: null,
        noDevices: true,
        deviceGroup: false,
        sendDisabled: true
    });

    await page.run('submitEmailSend()');
    assert.deepEqual(messageTexts(page, 'error'), ['Choose an ereader device.']);
    assert.equal(callsTo(page, 'POST', '/api/email/send').length, 0);

    page.click(buttonByText(page, '#emailSendNoDevices', 'Add Ereader Device'));
    await page.settle();
    assert.ok(!isModalOpen(page, 'emailSendModal'));
    assert.ok(isModalOpen(page, 'emailDevicesModal'));
});

test('an unconfigured mail server blocks sending with a hint', async t => {
    const page = await boot({ routes: { 'GET /api/email/status': emailStatus({ configured: false }) } });
    t.after(() => page.close());

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);

    const state = sendState(page);
    assert.equal(state.hint, '⚠️ Email delivery is not configured yet. Ask an administrator to set the SMTP host and from-address in Settings → Send to Ereader.');
    assert.ok(state.sendDisabled);
    assert.deepEqual(state.options.map(([value]) => value), ['1', '2']);

    // The status is cached for the session; the hint stays on the next open.
    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);
    assert.ok(sendState(page).sendDisabled);
    assert.equal(callsTo(page, 'GET', '/api/email/status').length, 1);
});

test('a failed status check does not block sending', async t => {
    const page = await boot({ routes: { 'GET /api/email/status': respond(500, 'Internal Server Error') } });
    t.after(() => page.close());

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);

    const state = sendState(page);
    assert.equal(state.hint, null);
    assert.equal(state.sendDisabled, false);
    assert.deepEqual(messages(page), []);
});

test('a failed device load is reported and blocks sending', async t => {
    const page = await boot({ routes: { 'GET /api/email/devices': respond(500, { error: 'Database unavailable' }) } });
    t.after(() => page.close());

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);

    assert.deepEqual(sendState(page).options, [['', 'Failed to load devices']]);
    assert.ok(sendState(page).sendDisabled);
    assert.deepEqual(messageTexts(page, 'error'), ['Failed to load ereader devices: Database unavailable']);
});

test('a failed builds check leaves the dialog usable', async t => {
    const page = await boot({ routes: { 'GET /api/email/condense-builds': () => { throw new TypeError('Failed to fetch'); } } });
    t.after(() => page.close());

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);
    await page.settle();

    assert.ok(page.$('#emailCondenseBuilds').hidden);
    assert.equal(sendState(page).sendDisabled, false);
    assert.ok(page.consoleMessages.some(entry => entry.type === 'warn' && entry.args[0] === 'Could not load condensed book builds'));
});

test('Email Selected needs a selection', async t => {
    const page = await boot();
    t.after(() => page.close());

    await page.run('openEmailSendModalForSelected()');

    assert.deepEqual(messageTexts(page, 'error'), ['Select at least one file to email.']);
    assert.ok(!isModalOpen(page, 'emailSendModal'));
});

test('Email Selected waits for a series that is still loading its files', async t => {
    const paperGirls = deferred();
    const page = await bootPage({
        hash: '#/library',
        routes: {
            ...EMAIL_ROUTES,
            'GET /api/files/series': {
                series: [seriesSummary(), seriesSummary({ id: 'paper-girls', title: 'Paper Girls', cover_file_path: 'Paper Girls/Paper Girls 001.cbz' })],
                total_series: 2
            },
            'GET /api/files/series/saga/issues': { issues: [ISSUES[0]], issue_count: 1, total_pages: 1, per_page: -1 },
            'GET /api/files/series/paper-girls/issues': () => paperGirls.promise
        }
    });
    t.after(() => page.close());

    page.click('.series-select-box[data-series-id="saga"]');
    await page.settle();
    assert.ok(!page.$('#seriesSelectionEmailBtn').disabled);
    page.click('.series-select-box[data-series-id="paper-girls"]');
    await page.settle();
    assert.ok(page.$('#seriesSelectionEmailBtn').disabled, 'the series toolbar waits for the files');
    assert.deepEqual(callsTo(page, 'GET', '/api/files/series/paper-girls/issues').map(call => call.query), [{ per_page: '-1', filter: 'all' }]);

    // The selection menu already has files to send and stays enabled.
    page.click('#emailSelectedItem');
    await page.settle();
    assert.ok(!isModalOpen(page, 'emailSendModal'), 'the dialog waits for the second series');

    paperGirls.resolve({ issues: [seriesIssue({ file_path: 'Paper Girls/Paper Girls 001.cbz' })], issue_count: 1, total_pages: 1, per_page: -1 });
    await page.settle();

    assert.ok(isModalOpen(page, 'emailSendModal'));
    assert.equal(page.$('#emailSendSummary').textContent, 'Send 2 selected files.');
    page.click('#emailSendSubmitBtn');
    await page.settle();
    assert.deepEqual(callsTo(page, 'POST', '/api/email/send')[0].body.files, [FIRST, 'Paper Girls/Paper Girls 001.cbz']);
});

test('Cancel, × and Escape close the dialog and forget what was being sent', async t => {
    const page = await boot();
    t.after(() => page.close());

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);
    page.click(buttonByText(page, '#emailSendModal .modal-footer', 'Cancel'));
    assert.ok(!isModalOpen(page, 'emailSendModal'));
    await page.run('submitEmailSend()');
    assert.equal(callsTo(page, 'POST', '/api/email/send').length, 0);

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);
    page.click('#emailSendModal .modal-header .close-btn');
    assert.ok(!isModalOpen(page, 'emailSendModal'));

    await page.run(`openEmailSendModalForFile(${JSON.stringify(FIRST)})`);
    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'emailSendModal'));
    assert.equal(page.run('currentEmailSend'), null);
});

test('the email actions are write actions for read-only users', async t => {
    const page = await boot({ user: { canModifyLibrary: false, canAdminister: false } });
    t.after(() => page.close());

    const card = page.$(`.series-issue-card[data-file-path="${FIRST}"]`);
    const issueItem = [...card.querySelectorAll('.dropdown-item')].find(item => item.textContent.trim() === 'Email to Ereader…');
    const seriesItems = [...page.$$('.series-actions-dropdown .dropdown-item')]
        .filter(item => ['Email All Issues', 'Auto-send New Issues'].includes(item.textContent.trim()));

    assert.ok(page.document.body.classList.contains('user-read-only'));
    assert.ok(issueItem.hasAttribute('data-requires-write'));
    assert.equal(seriesItems.length, 2);
    assert.ok(seriesItems.every(item => item.hasAttribute('data-requires-write')));
    assert.ok(page.$('#emailSendSubmitBtn').hasAttribute('data-requires-write'));
});
