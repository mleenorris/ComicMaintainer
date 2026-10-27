'use strict';

// js/app/email.js: condensing a send into books — the EPUB/AZW3 restriction
// of the format picker, the plan read-out (requests, books, oversized books,
// paging) that decides whether Send is allowed, and sending condensed.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const {
    emailDevice,
    emailStatus,
    condensedBook,
    condensePlan,
    condenseBuild,
    seriesIssue,
    bootSeriesPage,
    messages,
    isModalOpen,
    clickIssueAction,
    clickSeriesAction,
    buttonByText,
    callsTo
} = require('./support/email-helpers');

const FIRST = 'Saga/Saga 001.cbz';
const ISSUES = [
    seriesIssue(),
    seriesIssue({ file_path: 'Saga/Saga 002.cbz', file_name: 'Saga 002.cbz', title: 'Saga 002', issue: '2' })
];
const MB = 1024 * 1024;

const EMAIL_ROUTES = {
    'GET /api/email/status': emailStatus(),
    'GET /api/email/devices': {
        devices: [
            emailDevice({ id: 1, name: 'Kindle', emailAddress: 'reader@kindle.com' }),
            emailDevice({ id: 2, name: 'Kobo', emailAddress: 'me@kobo.com', deliveryFormat: 'epub' })
        ]
    },
    'GET /api/email/condense-builds': { builds: [] },
    'POST /api/email/condense-plan': condensePlan()
};

// The body every plan request of an "Email All Issues" dialog starts from.
const SERIES_PLAN = {
    seriesId: 'saga',
    condenseMode: 'count',
    issuesPerBook: 5,
    deliveryFormat: 'epub',
    deviceId: 1,
    skipAlreadyDelivered: true
};

async function openForSeries(t, { routes = {} } = {}) {
    const page = await bootSeriesPage({ issues: ISSUES, routes: { ...EMAIL_ROUTES, ...routes } });
    t.after(() => page.close());
    clickSeriesAction(page, 'Email All Issues');
    await page.settle();
    assert.ok(isModalOpen(page, 'emailSendModal'));
    return page;
}

async function choose(page, selector, value) {
    page.input(selector, value);
    await page.settle();
}

function planBodies(page) {
    return callsTo(page, 'POST', '/api/email/condense-plan').map(call => call.body);
}

function sendDisabled(page) {
    return page.$('#emailSendSubmitBtn').disabled;
}

function formatState(page) {
    const select = page.$('#emailSendFormatSelect');
    return {
        value: select.value,
        disabled: select.disabled,
        hint: !page.$('#emailSendFormatHint').hidden,
        offered: [...select.options].filter(option => !option.disabled && !option.hidden).map(option => option.value)
    };
}

/** The condense read-out: null while hidden, else its text or its parts. */
function planState(page) {
    const panel = page.$('#emailCondensePlan');
    if (panel.hidden) return null;
    const list = page.$('#emailCondenseBookList');
    if (!list) return { text: panel.textContent.trim() };
    const [heading, second] = panel.children;
    return {
        heading: heading.textContent.trim(),
        warning: second === list ? null : second.textContent.replace(/\s+/g, ' ').trim(),
        books: [...list.children].map(row => row.querySelector('span').textContent),
        downloads: list.querySelectorAll('button').length,
        more: page.$('#emailCondenseBookListMore').textContent.trim() || null
    };
}

test('condensing limits the format to EPUB or AZW3 and turning it off restores the earlier choice', async t => {
    const page = await openForSeries(t);
    const ALL_FORMATS = ['device', 'original', 'epub', 'azw3'];
    assert.deepEqual(formatState(page), { value: 'device', disabled: false, hint: false, offered: ALL_FORMATS });
    assert.ok(page.$('#emailCondenseCountGroup').hidden);

    await choose(page, '#emailSendFormatSelect', 'original');
    assert.deepEqual(planBodies(page), [], 'a format change while sending separately needs no plan');

    await choose(page, '#emailCondenseModeSelect', 'all');
    assert.deepEqual(formatState(page), { value: 'epub', disabled: false, hint: true, offered: ['epub', 'azw3'] });
    assert.ok(page.$('#emailCondenseCountGroup').hidden, 'one book of everything needs no count');
    assert.deepEqual(planBodies(page), [{ ...SERIES_PLAN, condenseMode: 'all' }]);

    await choose(page, '#emailCondenseModeSelect', 'count');
    assert.ok(!page.$('#emailCondenseCountGroup').hidden);
    assert.equal(formatState(page).value, 'epub');

    await choose(page, '#emailCondenseModeSelect', 'none');
    assert.deepEqual(formatState(page), { value: 'original', disabled: false, hint: false, offered: ALL_FORMATS });
    assert.ok(page.$('#emailCondenseCountGroup').hidden);
    assert.equal(planState(page), null);
    assert.equal(page.$('#emailCondensePlan').childElementCount, 0);
    assert.equal(planBodies(page).length, 2, 'sending separately needs no plan');
    assert.ok(!sendDisabled(page));
});

test('a format picked while condensing relabels the plan and is kept afterwards', async t => {
    const page = await openForSeries(t);

    await choose(page, '#emailCondenseModeSelect', 'count');
    assert.equal(planState(page).heading, '1 EPUB from 5 issues (attachment limit 25.0 MB).');

    await choose(page, '#emailSendFormatSelect', 'azw3');
    assert.equal(planState(page).heading, '1 AZW3 from 5 issues (attachment limit 25.0 MB).');
    assert.equal(planBodies(page).length, 1, 'the books themselves do not change');

    await choose(page, '#emailCondenseModeSelect', 'none');
    assert.equal(formatState(page).value, 'azw3', 'a deliberate choice outlives condensing');

    await choose(page, '#emailCondenseModeSelect', 'count');
    assert.equal(planBodies(page).at(-1).deliveryFormat, 'azw3');
    assert.equal(planState(page).heading, '1 AZW3 from 5 issues (attachment limit 25.0 MB).');
});

test('the plan is asked for with the settings the send would use', async t => {
    const page = await openForSeries(t);

    await choose(page, '#emailCondenseModeSelect', 'count');
    assert.equal(planState(page).heading, '1 EPUB from 5 issues (attachment limit 25.0 MB).');
    assert.deepEqual(planState(page).books, ['Saga #1-5 · 5 issues · ~10.0 MB']);
    assert.ok(!sendDisabled(page), 'a plan that fits can be emailed');

    await choose(page, '#emailCondenseCountInput', '1');
    await choose(page, '#emailCondenseCountInput', '900');
    await choose(page, '#emailCondenseCountInput', '');
    await choose(page, '#emailSendDeviceSelect', '2');
    await choose(page, '#emailSkipAlreadyDeliveredCheckbox', false);
    await choose(page, '#emailSendFormatSelect', 'azw3');
    await choose(page, '#emailCondenseCountInput', '3');

    assert.deepEqual(planBodies(page), [
        SERIES_PLAN,
        { ...SERIES_PLAN, issuesPerBook: 2 },
        { ...SERIES_PLAN, issuesPerBook: 500 },
        { ...SERIES_PLAN, issuesPerBook: 5 },
        { ...SERIES_PLAN, deviceId: 2 },
        { ...SERIES_PLAN, deviceId: 2, skipAlreadyDelivered: false },
        { ...SERIES_PLAN, deviceId: 2, skipAlreadyDelivered: false, deliveryFormat: 'azw3', issuesPerBook: 3 }
    ]);
});

test('a plan for one issue names the file, and no device until one is saved', async t => {
    const page = await bootSeriesPage({
        issues: ISSUES,
        routes: { ...EMAIL_ROUTES, 'GET /api/email/devices': { devices: [] } }
    });
    t.after(() => page.close());
    clickIssueAction(page, FIRST, 'Email to Ereader…');
    await page.settle();

    await choose(page, '#emailCondenseModeSelect', 'count');

    assert.deepEqual(planBodies(page), [{
        files: [FIRST],
        condenseMode: 'count',
        issuesPerBook: 5,
        deliveryFormat: 'epub',
        deviceId: null,
        skipAlreadyDelivered: true
    }]);
    assert.ok(planState(page).heading.startsWith('1 EPUB'));
    assert.ok(sendDisabled(page), 'a plan that fits does not make up for the missing device');
});

test('Send waits for both the devices and a plan that fits', async t => {
    const devices = deferred();
    const page = await bootSeriesPage({
        issues: ISSUES,
        routes: { ...EMAIL_ROUTES, 'GET /api/email/devices': () => devices.promise }
    });
    t.after(() => page.close());
    clickSeriesAction(page, 'Email All Issues');
    await page.settle();

    await choose(page, '#emailCondenseModeSelect', 'count');
    assert.equal(planBodies(page)[0].deviceId, null, 'no device is chosen while they load');
    assert.ok(planState(page).heading);
    assert.ok(sendDisabled(page), 'the devices are still loading');

    devices.resolve({ devices: [emailDevice()] });
    await page.settle();
    assert.ok(!sendDisabled(page), 'the plan fits, so Send is allowed once a device is there');

    page.route('POST /api/email/condense-plan', condensePlan({
        books: [condensedBook({ exceeds_attachment_limit: true, estimated_bytes: 30 * MB })]
    }));
    await choose(page, '#emailCondenseCountInput', '6');
    assert.ok(sendDisabled(page), 'a plan with an oversized book blocks Send');
});

test('a plan that does not fit warns, blocks Send and still offers every book as a download', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const books = Array.from({ length: 12 }, (_, index) => condensedBook({
        display_name: `Saga #${index * 5 + 1}-${index * 5 + 5}`
    }));
    books[3] = { ...books[3], exceeds_attachment_limit: true, estimated_bytes: 30 * MB };
    books[11] = { ...books[11], issue_count: 1, display_name: 'Saga #56', exceeds_attachment_limit: true, estimated_bytes: 40 * MB };
    const page = await openForSeries(t, {
        routes: {
            'POST /api/email/condense-plan': condensePlan({ books }),
            'POST /api/email/condense-builds': condenseBuild({ status: 'queued', display_name: 'Saga #56', issue_count: 1 })
        }
    });

    await choose(page, '#emailCondenseModeSelect', 'count');

    let plan = planState(page);
    assert.equal(plan.heading, '12 EPUBs from 56 issues (attachment limit 25.0 MB).');
    assert.equal(plan.warning,
        '⚠️ 2 of these books are larger than the 25.0 MB attachment limit and cannot be emailed. ' +
        'Download them instead, or condense fewer issues per book.');
    assert.equal(plan.books.length, 10, 'the first page of books');
    assert.equal(plan.downloads, 10);
    assert.equal(plan.books[3], 'Saga #16-20 · 5 issues · ~30.0 MB · too large to email');
    assert.equal(plan.more, 'Show more (2 left)');
    assert.ok(sendDisabled(page));

    page.click(buttonByText(page, '#emailCondenseBookListMore', 'Show more (2 left)'));
    plan = planState(page);
    assert.equal(plan.books.length, 12);
    assert.equal(plan.books[11], 'Saga #56 · 1 issue · ~40.0 MB · too large to email');
    assert.equal(plan.more, null);

    // The oversized book at the end of the list can still be built.
    page.click(page.$$('#emailCondenseBookList button')[11]);
    await page.settle();
    assert.deepEqual(callsTo(page, 'POST', '/api/email/condense-builds').map(call => call.body),
        [{ ...SERIES_PLAN, bookIndex: 11 }]);
});

test('a single oversized book is described in the singular', async t => {
    const page = await openForSeries(t, {
        routes: {
            'POST /api/email/condense-plan': condensePlan({
                books: [condensedBook({ display_name: 'Saga #1', issue_count: 1, exceeds_attachment_limit: true, estimated_bytes: 30 * MB })]
            })
        }
    });

    await choose(page, '#emailCondenseModeSelect', 'all');

    const plan = planState(page);
    assert.equal(plan.heading, '1 EPUB from 1 issue (attachment limit 25.0 MB).');
    assert.match(plan.warning, /^⚠️ 1 of these books? is larger than the 25\.0 MB attachment limit and cannot be emailed\. Download it instead/);
    assert.equal(plan.more, null);
    assert.ok(sendDisabled(page));
});

test('an empty or failed plan blocks Send until condensing is turned off', async t => {
    const page = await openForSeries(t, { routes: { 'POST /api/email/condense-plan': condensePlan({ books: [] }) } });

    await choose(page, '#emailCondenseModeSelect', 'count');
    assert.deepEqual(planState(page), { text: 'Nothing to condense in this selection.' });
    assert.ok(sendDisabled(page));

    page.route('POST /api/email/condense-plan', { mode: 'count', can_email: true });
    await choose(page, '#emailCondenseCountInput', '4');
    assert.deepEqual(planState(page), { text: 'Nothing to condense in this selection.' }, 'a plan without a book list');

    page.route('POST /api/email/condense-plan', () => { throw new TypeError('Failed to fetch'); });
    await choose(page, '#emailCondenseCountInput', '6');
    assert.deepEqual(planState(page), { text: 'Could not calculate the condensed books: Failed to fetch' });

    page.route('POST /api/email/condense-plan', respond(400, { error: 'Choose at least two issues per book' }));
    await choose(page, '#emailCondenseCountInput', '7');
    assert.deepEqual(planState(page), { text: 'Could not calculate the condensed books: Choose at least two issues per book' });
    assert.ok(sendDisabled(page));

    await choose(page, '#emailCondenseModeSelect', 'none');
    assert.equal(planState(page), null);
    assert.ok(!sendDisabled(page));
    assert.deepEqual(messages(page), [], 'plan problems are shown in the dialog, not as toasts');
});

test('only the newest plan is shown when answers arrive out of order', async t => {
    const answers = [deferred(), deferred(), deferred(), deferred()];
    let next = 0;
    const page = await openForSeries(t, { routes: { 'POST /api/email/condense-plan': () => answers[next++].promise } });

    await choose(page, '#emailCondenseModeSelect', 'count');
    assert.deepEqual(planState(page), { text: 'Calculating condensed books...' });
    assert.ok(sendDisabled(page));

    await choose(page, '#emailCondenseCountInput', '2');
    answers[1].resolve(condensePlan({ books: [condensedBook({ display_name: 'Newer', issue_count: 2 })] }));
    await page.settle();
    answers[0].resolve(condensePlan({ books: [condensedBook({ display_name: 'Older' })] }));
    await page.settle();
    assert.deepEqual(planState(page).books, ['Newer · 2 issues · ~10.0 MB']);

    await choose(page, '#emailCondenseCountInput', '3');
    await choose(page, '#emailCondenseCountInput', '4');
    answers[3].resolve(condensePlan({ books: [condensedBook({ display_name: 'Newest', issue_count: 4 })] }));
    await page.settle();
    answers[2].reject(new TypeError('Failed to fetch'));
    await page.settle();

    assert.deepEqual(planState(page).books, ['Newest · 4 issues · ~10.0 MB'], 'a late failure of an older plan is ignored');
    assert.ok(!sendDisabled(page));
});

test('sending condensed includes the condense settings, and reopening starts over', async t => {
    const page = await openForSeries(t, {
        routes: { 'POST /api/email/send-series': { queued: [{ id: 1, filePath: 'Saga #1-3', status: 'pending' }], skipped: {} } }
    });

    await choose(page, '#emailSendFormatSelect', 'original');
    await choose(page, '#emailCondenseModeSelect', 'count');
    await choose(page, '#emailCondenseCountInput', '3');
    page.click('#emailSendSubmitBtn');
    await page.settle();

    assert.deepEqual(callsTo(page, 'POST', '/api/email/send-series').map(call => call.body), [{
        seriesId: 'saga',
        deviceId: '1',
        deliveryFormat: 'epub',
        skipAlreadyDelivered: true,
        condenseMode: 'count',
        issuesPerBook: 3
    }]);
    assert.ok(!isModalOpen(page, 'emailSendModal'));
    assert.deepEqual(messages(page), [{ text: 'Queued 1 email delivery.', type: 'success' }]);

    clickSeriesAction(page, 'Email All Issues');
    await page.settle();
    assert.equal(page.$('#emailCondenseModeSelect').value, 'none');
    assert.ok(page.$('#emailCondenseCountGroup').hidden);
    assert.equal(planState(page), null);
    assert.deepEqual(formatState(page), {
        value: 'original',
        disabled: false,
        hint: false,
        offered: ['device', 'original', 'epub', 'azw3']
    });
    assert.ok(!sendDisabled(page));
});
