'use strict';

// Settings menu → Processing History (js/app/settings.js): paging through the
// history, how each entry describes what changed, and the failure states.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const { bootSettings, requestLog } = require('./support/settings-helpers');

const T0 = 1700000000;

function historyItem(i) {
    return {
        timestamp: T0 + i,
        filepath: `/comics/Series/Issue ${i}.cbz`,
        operation_type: 'normalize',
        success: true,
        error_message: null,
        before_filename: `Issue ${i}.cbz`,
        after_filename: `Series #${String(i).padStart(3, '0')}.cbz`
    };
}

/** GET /api/processing-history over `total` generated entries, paged like the server. */
const historyRoute = total => call => {
    const limit = Number(call.query.limit);
    const offset = Number(call.query.offset);
    const history = [];
    for (let i = offset; i < Math.min(offset + limit, total); i++) history.push(historyItem(i));
    return { history, total };
};

const pager = page => ({
    info: page.$('#historyPageInfo').textContent,
    prev: page.$('#historyPrevBtn').disabled ? 'disabled' : 'enabled',
    next: page.$('#historyNextBtn').disabled ? 'disabled' : 'enabled',
    loading: page.$('#historyLoadingIndicator').style.display,
    entries: page.$$('#historyContent > div > div').length
});

/** One rendered history entry, read back the way a user would see it. */
function entrySummary(entry) {
    const [header, ...rest] = entry.children;
    const grid = rest.at(-1);
    const errorBox = rest.length > 1 ? rest[0] : null;
    const icon = header.querySelector('span[title]');
    return {
        status: `${icon.textContent} ${icon.title}`,
        file: header.querySelector('strong').textContent,
        when: header.firstElementChild.lastElementChild.textContent,
        operation: header.lastElementChild.textContent,
        error: errorBox ? [errorBox.firstElementChild.textContent, errorBox.lastElementChild.textContent] : null,
        changes: [...grid.children].map(change => change.children.length
            ? [
                change.firstElementChild.textContent,
                ...[...change.lastElementChild.children]
                    .filter(side => side.tagName === 'DIV')
                    .map(side => `${side.firstElementChild.textContent} ${side.lastElementChild.textContent}`)
            ]
            : change.textContent.trim())
    };
}

test('Processing History pages through the history 50 entries at a time', async () => {
    const page = await bootSettings({ routes: { 'GET /api/processing-history': historyRoute(120) } });

    page.click('#settingsMenuToggle');
    let since = page.fetchCalls.length;
    page.click('#settingsDropdownMenu button[onclick^="openProcessingHistoryModal()"]');
    assert.equal(page.$('#processingHistoryModal').classList.contains('active'), true);
    assert.equal(page.$('#settingsDropdownMenu').classList.contains('show'), false);
    await page.settle();

    assert.deepEqual(requestLog(page, since), ['GET /api/processing-history?limit=50&offset=0']);
    assert.deepEqual(pager(page), { info: 'Page 1 of 3 (120 total)', prev: 'disabled', next: 'enabled', loading: 'none', entries: 50 });
    assert.equal(entrySummary(page.$('#historyContent > div > div')).file, 'Series #000.cbz');

    // There is nothing before the first page.
    since = page.fetchCalls.length;
    await page.run('loadPreviousHistoryPage()');
    assert.deepEqual(requestLog(page, since), []);

    page.click('#historyNextBtn');
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/processing-history?limit=50&offset=50']);
    assert.deepEqual(pager(page), { info: 'Page 2 of 3 (120 total)', prev: 'enabled', next: 'enabled', loading: 'none', entries: 50 });

    since = page.fetchCalls.length;
    page.click('#historyNextBtn');
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/processing-history?limit=50&offset=100']);
    assert.deepEqual(pager(page), { info: 'Page 3 of 3 (120 total)', prev: 'enabled', next: 'disabled', loading: 'none', entries: 20 });
    assert.equal(entrySummary(page.$$('#historyContent > div > div').at(-1)).file, 'Series #119.cbz');

    // ...and nothing after the last.
    since = page.fetchCalls.length;
    await page.run('loadNextHistoryPage()');
    assert.deepEqual(requestLog(page, since), []);

    page.click('#historyPrevBtn');
    await page.settle();
    assert.deepEqual(requestLog(page, since), ['GET /api/processing-history?limit=50&offset=50']);
    assert.equal(pager(page).info, 'Page 2 of 3 (120 total)');

    page.click('#processingHistoryModal .close-btn');
    assert.equal(page.$('#processingHistoryModal').classList.contains('active'), false);

    // Reopening starts from the newest entries again.
    since = page.fetchCalls.length;
    await page.run('openProcessingHistoryModal()');
    assert.deepEqual(requestLog(page, since), ['GET /api/processing-history?limit=50&offset=0']);
    assert.equal(pager(page).info, 'Page 1 of 3 (120 total)');
    page.click('#processingHistoryModal .modal-footer button[onclick="closeProcessingHistoryModal()"]');
    assert.equal(page.$('#processingHistoryModal').classList.contains('active'), false);
});

test('each history entry shows its outcome and the before/after of every changed field', async () => {
    const history = [
        {
            timestamp: T0,
            filepath: '/comics/old.cbz',
            operation_type: 'rename <all>',
            success: true,
            error_message: 'ignored for successful entries',
            before_filename: 'old.cbz',
            after_filename: 'New <b>Name</b>.cbz',
            before_title: null,
            after_title: 'Title & "Quote"',
            before_series: 'Same Series',
            after_series: 'Same Series',
            before_issue: '1',
            after_issue: '001',
            before_publisher: 'Pub',
            after_publisher: null,
            before_year: 2020,
            after_year: 2021,
            before_volume: null
        },
        {
            timestamp: T0 + 3600,
            filepath: '/comics/broken.cbz',
            operation_type: 'normalize',
            success: false,
            error_message: '<script>alert(1)</script> disk full',
            before_filename: null,
            after_filename: null
        },
        {
            timestamp: T0 + 7200,
            filepath: '/comics/same.cbz',
            operation_type: 'normalize',
            success: true,
            before_filename: 'same.cbz',
            after_filename: 'same.cbz'
        },
        {
            timestamp: T0 + 10800,
            filepath: '/comics/fallback.cbz',
            operation_type: 'rename',
            success: false,
            error_message: '',
            before_filename: 'before-only.cbz',
            after_filename: null
        },
        {
            // Tagging only: no filename either side (an empty and a missing
            // value are not a change), and a title that was not there before.
            timestamp: T0 + 14400,
            filepath: '/comics/tagged.cbz',
            operation_type: 'metadata',
            success: true,
            before_filename: '',
            after_filename: null,
            before_title: null,
            after_title: 'Tagged Title'
        }
    ];
    const page = await bootSettings({ routes: { 'GET /api/processing-history': { history, total: 5 } } });

    await page.run('openProcessingHistoryModal()');
    assert.deepEqual(pager(page), { info: 'Page 1 of 1 (5 total)', prev: 'disabled', next: 'disabled', loading: 'none', entries: 5 });

    const when = offset => page.evaluate(`new Date((${T0} + ${offset}) * 1000).toLocaleString()`);
    const entries = page.$$('#historyContent > div > div');
    assert.deepEqual(entries.map(entrySummary), [
        {
            status: '✅ Success',
            file: 'New <b>Name</b>.cbz',
            when: when(0),
            operation: 'rename <all>',
            error: null,
            changes: [
                ['Filename', 'Before: old.cbz', 'After: New <b>Name</b>.cbz'],
                ['Title', 'Before: (empty)', 'After: Title & "Quote"'],
                ['Issue', 'Before: 1', 'After: 001'],
                ['Publisher', 'Before: Pub', 'After: (empty)'],
                ['Year', 'Before: 2020', 'After: 2021']
            ]
        },
        {
            status: '❌ Failed',
            file: '/comics/broken.cbz',
            when: when(3600),
            operation: 'normalize',
            error: ['Error', '<script>alert(1)</script> disk full'],
            changes: []
        },
        {
            status: '✅ Success',
            file: 'same.cbz',
            when: when(7200),
            operation: 'normalize',
            error: null,
            changes: ['No changes made']
        },
        {
            status: '❌ Failed',
            file: 'before-only.cbz',
            when: when(10800),
            operation: 'rename',
            error: null,
            changes: [['Filename', 'Before: before-only.cbz', 'After: (empty)']]
        },
        {
            status: '✅ Success',
            file: '/comics/tagged.cbz',
            when: when(14400),
            operation: 'metadata',
            error: null,
            changes: [['Title', 'Before: (empty)', 'After: Tagged Title']]
        }
    ]);

    // Server-provided text is escaped, never interpreted as markup.
    assert.equal(page.$('#historyContent b'), null);
    assert.equal(page.$('#historyContent script'), null);
    assert.equal(page.$$('#historyContent em').map(em => em.textContent).join(','), '(empty),(empty),(empty),(empty)');
});

test('an empty or failing history is explained in the panel', async () => {
    const page = await bootSettings({ routes: { 'GET /api/processing-history': { history: [], total: 0 } } });
    const content = () => page.$('#historyContent').textContent.trim();

    await page.run('openProcessingHistoryModal()');
    assert.equal(content(), 'No processing history found');
    assert.deepEqual(pager(page), { info: 'No results', prev: 'disabled', next: 'disabled', loading: 'none', entries: 0 });

    for (const [failure, message] of [
        [respond(500, 'Error retrieving processing history'), 'Error loading history: HTTP error! status: 500'],
        [respond(400, 'limit must be positive'), 'Error loading history: HTTP error! status: 400'],
        [() => { throw new TypeError('Failed to fetch'); }, 'Error loading history: Failed to fetch']
    ]) {
        page.route('GET /api/processing-history', failure);
        await page.run('loadProcessingHistory()');
        assert.equal(content(), message);
        assert.equal(pager(page).loading, 'none');
    }

    // While a page loads, the previous entries are cleared and a spinner shown.
    const pending = deferred();
    page.route('GET /api/processing-history', () => pending.promise);
    const loading = page.run('loadProcessingHistory()');
    await page.settle();
    assert.equal(content(), '');
    assert.equal(pager(page).loading, 'block');
    pending.resolve({ history: [historyItem(7)], total: 1 });
    await loading;
    assert.deepEqual(pager(page), { info: 'Page 1 of 1 (1 total)', prev: 'disabled', next: 'disabled', loading: 'none', entries: 1 });

    // An expired session goes to the login page instead of rendering anything.
    page.route('GET /api/processing-history', 401);
    await page.run('loadProcessingHistory()');
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
    assert.equal(content(), '');
});
