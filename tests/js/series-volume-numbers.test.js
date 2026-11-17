'use strict';

// js/app/series.js: Find Volume Numbers. A volume of the open series known
// only by the year it started (volume_key "y2016") is looked up on ComicVine
// (GET /api/metadata/volume-number), which suggests its number from the
// same-named volumes of its publisher. Applying it starts the same
// update-metadata job the batch Update dialog does, setting <Volume>.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    bootSeriesPage,
    seriesSummary,
    seriesIssue,
    messageTexts,
    isModalOpen,
    pressEscape,
    buttonByText,
    clickSeriesAction,
    callsTo
} = require('./support/email-helpers');

const SERIES = seriesSummary({ id: 'batman', title: 'Batman', issue_count: 4 });
const LOOKUP_2016 = 'GET /api/metadata/volume-number?title=Batman&year=2016';
const LOOKUP_2025 = 'GET /api/metadata/volume-number?title=Batman&year=2025';
const JOB = 'POST /api/jobs/update-metadata-selected';

function issue(path, number, volumeKey) {
    return seriesIssue({ file_path: path, file_name: path, title: path, issue: number, volume_key: volumeKey });
}

// Two year-only volumes, listed newest first, plus an issue with a number and
// one without any volume.
const ISSUES = [
    issue('Batman 2025 001.cbz', '1', 'y2025'),
    issue('Batman 2016 001.cbz', '1', 'y2016'),
    issue('Batman 2016 002.cbz', '2', 'y2016'),
    issue('Batman v1 001.cbz', '1', 'v1'),
    issue('Batman 001.cbz', '1', null)
];

// GET /api/metadata/volume-number (MetadataController.VolumeNumber): DC's
// earlier "Batman" volumes as ComicVine lists them, a one-shot (not counted)
// and a short 2012 volume readers do not count among them.
function suggestion(overrides = {}) {
    return {
        title: 'Batman',
        year: 2016,
        number: 4,
        publisher: 'DC Comics',
        volumes: [
            { name: 'Batman', start_year: 1940, issues: 713, publisher: 'DC Comics', site_url: 'https://comicvine.example/1940', counted: true, matched: false },
            { name: 'Batman', start_year: 1989, issues: 1, publisher: 'DC Comics', site_url: 'javascript:alert(1)', counted: false, matched: false },
            { name: 'Batman', start_year: 2011, issues: 57, publisher: 'DC Comics', site_url: null, counted: true, matched: false },
            { name: 'Batman', start_year: 2012, issues: null, publisher: 'DC Comics', site_url: null, matched: false },
            { name: 'Batman', start_year: 2016, issues: 163, publisher: 'DC Comics', site_url: null, counted: true, matched: true }
        ],
        ...overrides
    };
}

const LISTED = 'Batman (1940) · 713 issues Batman (1989) · 1 issue Batman (2011) · 57 issues Batman (2012) ' +
    'Batman (2016) · 163 issues (this volume)';

// The tick of each listed volume: [checked, disabled].
function ticks(page, year) {
    return [...row(page, year).querySelectorAll('.volume-number-counted input')].map(box => [box.checked, box.disabled]);
}

function tick(page, year, index, checked) {
    const box = row(page, year).querySelectorAll('.volume-number-counted input')[index];
    box.checked = checked;
    box.dispatchEvent(new page.window.Event('change', { bubbles: true }));
}

const NONE = { title: 'Batman', year: 2025, number: null, publisher: null, volumes: [] };

function boot(options = {}) {
    return bootSeriesPage({ series: SERIES, issues: ISSUES, ...options });
}

// The text of a node, with the counted volumes (list items) apart.
function textOf(node) {
    if (node.nodeType !== 1) return node.textContent;
    if (node.tagName === 'LI') return ` ${node.textContent} `;
    return [...node.childNodes].map(textOf).join('');
}

function rows(page) {
    return page.$$('#volumeNumbersList .volume-number-row').map(row => ({
        heading: row.querySelector('.volume-number-heading').textContent.trim(),
        result: textOf(row.querySelector('.volume-number-result')).replace(/\s+/g, ' ').trim()
    }));
}

// The volume-number lookups asked for, by URL.
function lookups(page) {
    return page.fetchCalls.filter(call => call.path === '/api/metadata/volume-number').map(call => call.url);
}

function row(page, year) {
    return page.$(`#volumeNumbersList [data-volume-year="${year}"]`);
}

async function openVolumeNumbers(page) {
    clickSeriesAction(page, 'Find Volume Numbers');
    await page.settle();
}

test('Find Volume Numbers lists each year-only volume and ComicVine\'s suggestion for it', async t => {
    const page = await boot({ routes: { [LOOKUP_2016]: suggestion(), [LOOKUP_2025]: NONE } });
    t.after(() => page.close());

    await openVolumeNumbers(page);

    assert.ok(isModalOpen(page, 'volumeNumbersModal'));
    assert.equal(page.$('#volumeNumbersModalTitle').textContent, 'Volume numbers for "Batman"');
    assert.equal(page.$('#volumeNumbersEmptyState').style.display, 'none');
    assert.deepEqual(rows(page), [
        {
            heading: '(2016) · 2 issues',
            result: `ComicVine suggests Vol. 4: the fourth "Batman" from DC Comics. ${LISTED} Set Vol. 4 on 2 issues`
        },
        { heading: '(2025) · 1 issue', result: 'ComicVine has no volume named "Batman" that started in 2025.' }
    ]);
    // Only http(s) links are kept.
    const links = [...row(page, 2016).querySelectorAll('a')];
    assert.deepEqual(links.map(link => link.getAttribute('href')), ['https://comicvine.example/1940']);
    assert.equal(links[0].getAttribute('target'), '_blank');
    // The one-shot starts unticked; a volume the server did not flag counts; the volume itself always counts.
    assert.deepEqual(ticks(page, 2016), [[true, false], [false, false], [true, false], [true, false], [true, true]]);
    // The volumes are asked for oldest first, one at a time.
    assert.deepEqual(lookups(page), [
        '/api/metadata/volume-number?title=Batman&year=2016',
        '/api/metadata/volume-number?title=Batman&year=2025'
    ]);
});

test('Find Volume Numbers words a suggestion without a publisher or a list, and a number past ten', async t => {
    const page = await boot({
        routes: {
            [LOOKUP_2016]: suggestion({ publisher: null, volumes: null }),
            [LOOKUP_2025]: suggestion({ number: 12, year: 2025 })
        }
    });
    t.after(() => page.close());

    await openVolumeNumbers(page);

    assert.deepEqual(rows(page).map(item => item.result), [
        'ComicVine suggests Vol. 4. Set Vol. 4 on 2 issues',
        `ComicVine suggests Vol. 12: the 12th "Batman" from DC Comics. ${LISTED} Set Vol. 12 on 1 issue`
    ]);
});

test('Ticking and unticking the volumes ComicVine listed recounts the number', async t => {
    const page = await boot({
        routes: {
            [LOOKUP_2016]: suggestion(),
            [LOOKUP_2025]: NONE,
            [JOB]: { job_id: 'job-5', total_items: 2 }
        }
    });
    t.after(() => page.close());

    await openVolumeNumbers(page);
    // The short 2012 volume does not count...
    tick(page, 2016, 3, false);
    assert.equal(rows(page)[0].result, `ComicVine suggests Vol. 3: the third "Batman" from DC Comics. ${LISTED} Set Vol. 3 on 2 issues`);
    // ...the one-shot does, for this reader.
    tick(page, 2016, 1, true);
    tick(page, 2016, 3, false);
    assert.match(rows(page)[0].result, /^ComicVine suggests Vol\. 4: the fourth .* Set Vol\. 4 on 2 issues$/);
    tick(page, 2016, 1, false);

    page.click(buttonByText(page, row(page, 2016), 'Set Vol. 3 on 2 issues'));
    await page.settle();
    const [call] = callsTo(page, 'POST', '/api/jobs/update-metadata-selected');
    assert.equal(call.body.Metadata.Volume, '3');

    // A recount once the dialog is closed changes nothing.
    await page.run('recountVolumeNumber(2016)');
});

test('Find Volume Numbers says when ComicVine could not be asked', async t => {
    const page = await boot({
        routes: {
            [LOOKUP_2016]: respond(503, { error: 'ComicVine is limiting requests' }),
            [LOOKUP_2025]: NONE
        }
    });
    t.after(() => page.close());

    await openVolumeNumbers(page);

    assert.equal(rows(page)[0].result, 'ComicVine could not be asked: ComicVine is limiting requests');
    assert.equal(rows(page)[1].result, 'ComicVine has no volume named "Batman" that started in 2025.');
});

test('Find Volume Numbers with an expired session goes to login', async t => {
    const page = await boot({ routes: { [LOOKUP_2016]: 401, [LOOKUP_2025]: NONE } });
    t.after(() => page.close());

    await openVolumeNumbers(page);

    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.equal(rows(page)[0].result, 'Asking ComicVine...');
});

test('Find Volume Numbers on a series with no year-only volume says so and asks nothing', async t => {
    const page = await boot({ issues: [issue('Batman v1 001.cbz', '1', 'v1'), issue('Batman 001.cbz', '1', 7)] });
    t.after(() => page.close());

    await openVolumeNumbers(page);

    assert.ok(isModalOpen(page, 'volumeNumbersModal'));
    assert.equal(page.$('#volumeNumbersEmptyState').style.display, 'block');
    assert.deepEqual(rows(page), []);
    assert.deepEqual(lookups(page), []);

    // A series whose issues are not loaded has none either.
    await page.run('openVolumeNumbersModal("unknown", "Unknown")');
    assert.equal(page.$('#volumeNumbersEmptyState').style.display, 'block');
});

test('Closing Find Volume Numbers stops asking ComicVine and drops a late answer', async t => {
    const answer = deferred();
    const page = await boot({ routes: { [LOOKUP_2016]: () => answer.promise, [LOOKUP_2025]: NONE } });
    t.after(() => page.close());

    await openVolumeNumbers(page);
    assert.equal(rows(page)[0].result, 'Asking ComicVine...');
    page.click(buttonByText(page, '#volumeNumbersModal .modal-footer', 'Close'));
    answer.resolve(suggestion());
    await page.settle();

    assert.ok(!isModalOpen(page, 'volumeNumbersModal'));
    assert.equal(rows(page)[0].result, 'Asking ComicVine...');
    assert.deepEqual(lookups(page), ['/api/metadata/volume-number?title=Batman&year=2016']);

    // Escape closes it too.
    await openVolumeNumbers(page);
    await pressEscape(page);
    assert.ok(!isModalOpen(page, 'volumeNumbersModal'));
});

test('Set Vol. N starts an update-metadata job that writes the number into the volume\'s issues', async t => {
    const page = await boot({
        routes: {
            [LOOKUP_2016]: suggestion(),
            [LOOKUP_2025]: NONE,
            [JOB]: { job_id: 'job-4', total_items: 2 },
            'GET /api/jobs/job-4': { job_id: 'job-4', status: 'running', total_items: 2, processed_items: 0, results: [] }
        }
    });
    t.after(() => page.close());

    await openVolumeNumbers(page);
    page.click(buttonByText(page, row(page, 2016), 'Set Vol. 4 on 2 issues'));
    await page.settle();

    assert.ok(!isModalOpen(page, 'volumeNumbersModal'));
    const [call] = callsTo(page, 'POST', '/api/jobs/update-metadata-selected');
    assert.deepEqual(JSON.parse(JSON.stringify(call.body)), {
        Files: ['Batman 2016 001.cbz', 'Batman 2016 002.cbz'],
        Metadata: { Volume: '4' },
        Fields: ['volume']
    });
    assert.deepEqual(messageTexts(page, 'info'), ['Setting Vol. 4 on 2 issues of Batman (2016)']);
    assert.ok(isModalOpen(page, 'progressModal'));
    assert.equal(page.$('#progressTitle').textContent, 'Setting the Volume Number...');
    assert.equal(callsTo(page, 'GET', '/api/jobs/job-4').length, 1);

    // While that job runs no second one starts.
    await openVolumeNumbers(page);
    await page.run('applyVolumeNumber(2016)');
    assert.equal(callsTo(page, 'POST', '/api/jobs/update-metadata-selected').length, 1);
    assert.match(messageTexts(page, 'warning')[0], /Another job is already in progress/);
});

test('Set Vol. N reports a job the server did not start, and an expired session goes to login', async t => {
    const page = await boot({ routes: { [LOOKUP_2016]: suggestion(), [LOOKUP_2025]: NONE, [JOB]: respond(500, { error: 'boom' }) } });
    t.after(() => page.close());

    await openVolumeNumbers(page);
    await page.run('applyVolumeNumber(2016)');

    assert.deepEqual(messageTexts(page, 'error'), ['Failed to set the volume number: boom']);
    assert.ok(!isModalOpen(page, 'progressModal'));

    page.route(JOB, 401);
    await openVolumeNumbers(page);
    await page.run('applyVolumeNumber(2016)');
    assert.ok(page.navigations.some(nav => nav.url.includes('/login.html')));
    assert.deepEqual(messageTexts(page, 'error'), ['Failed to set the volume number: boom']);
});

test('Set Vol. N does nothing for a volume without a suggestion or once the dialog is closed', async t => {
    const page = await boot({ routes: { [LOOKUP_2016]: suggestion(), [LOOKUP_2025]: NONE } });
    t.after(() => page.close());

    await openVolumeNumbers(page);
    await page.run('applyVolumeNumber(2025)');
    await page.run('applyVolumeNumber(1999)');
    await page.run('closeVolumeNumbersModal()');
    await page.run('applyVolumeNumber(2016)');

    assert.equal(callsTo(page, 'POST', '/api/jobs/update-metadata-selected').length, 0);
});
