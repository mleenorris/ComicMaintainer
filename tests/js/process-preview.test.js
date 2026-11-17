'use strict';

// Preview Changes (js/app/process-preview.js): the Process menu's Preview
// Process All / Preview Process Selected and the series Actions menu's
// Preview Changes start a preview job (POST /api/files/process-preview),
// followed in the progress modal. When it ends, its results
// (GET /api/files/process-preview/{jobId}) open grouped by series, and the
// ticked files are processed with the usual process-selected job, forced.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { respond } = require('./support/dom');
const h = require('./support/jobs-helpers');
const { bootSeriesPage, seriesSummary, clickSeriesAction } = require('./support/email-helpers');

const { JOB_ID, OTHER_JOB_ID, ISSUE_A, ISSUE_B } = h;
const START = '/api/files/process-preview';
const RESULTS = `/api/files/process-preview/${JOB_ID}`;
const PROCESS = '/api/jobs/process-selected';

// A file of a preview as ProcessPreviewController sends it.
function item(filePath, overrides = {}) {
    return {
        filePath,
        fileName: filePath.split('/').pop(),
        series: 'Batman',
        newFilePath: null,
        newFileName: null,
        renamed: false,
        tagChanges: [],
        clash: null,
        clashWith: null,
        warning: null,
        error: null,
        changed: true,
        ...overrides
    };
}

const RENAMED = item('/w/Batman/batman 1.cbz', {
    newFilePath: '/w/Batman/Batman - Chapter 0001.cbz',
    newFileName: 'Batman - Chapter 0001.cbz',
    renamed: true,
    tagChanges: [
        { field: 'Series', before: 'batman', after: 'Batman' },
        { field: 'VolumeYear', before: null, after: '2016' },
        { field: 'Volume', before: '2016', after: null }
    ]
});
const TAKEN = item('/w/Batman/batman 2.cbz', {
    newFilePath: '/w/Batman/Batman - Chapter 0002.cbz',
    newFileName: 'Batman - Chapter 0002.cbz',
    clash: 'existing',
    clashWith: '/w/Batman/Batman - Chapter 0002.cbz'
});
const SECOND = item('/w/Batman/two.cbz', {
    newFilePath: '/w/Batman/Batman - Chapter 0001.cbz',
    newFileName: 'Batman - Chapter 0001.cbz',
    clash: 'preview',
    clashWith: '/w/Batman/batman 1.cbz'
});
const FAILING = item('/w/Loose/x.cbz', { series: null, error: 'Loose file in the library root' });
const UNWRITTEN = item('/w/Saga/saga 1.cbr', {
    series: 'Saga',
    newFilePath: '/w/Saga/Saga - Chapter 0001.cbr',
    newFileName: 'Saga - Chapter 0001.cbr',
    renamed: true,
    warning: 'Not normalized: this CBR/RAR/ZIP was left unchanged'
});
const UNCHANGED = item('/w/Saga/Saga - Chapter 0002.cbz', { series: 'Saga', changed: false });
const ITEMS = [RENAMED, TAKEN, SECOND, FAILING, UNWRITTEN, UNCHANGED];

function summaryOf(items) {
    return {
        checked: items.length,
        changed: items.filter(entry => entry.changed).length,
        renamed: items.filter(entry => entry.renamed).length,
        tagsChanged: items.filter(entry => entry.tagChanges.length).length,
        clashes: items.filter(entry => entry.clash).length,
        unchanged: items.filter(entry => !entry.changed).length,
        errors: items.filter(entry => entry.error).length
    };
}

// GET /api/files/process-preview/{jobId}: pages of `items`, at most
// `pageSize` at a time whatever the limit asked for.
function previewServer(items, { status = 'completed', pageSize = 1000, total } = {}) {
    return call => {
        const filter = call.query.filter;
        const offset = Number(call.query.offset);
        const listed = filter === 'all' ? items : items.filter(entry => entry.changed);
        const count = Math.min(Number(call.query.limit), pageSize);
        return {
            jobId: JOB_ID,
            status,
            finished: status === 'completed',
            totalFiles: items.length,
            summary: summaryOf(items),
            filter,
            offset,
            limit: count,
            total: total ?? listed.length,
            items: listed.slice(offset, offset + count)
        };
    };
}

function routes(extra = {}) {
    return {
        [`POST ${START}`]: { job_id: JOB_ID, total_items: ITEMS.length, jobId: JOB_ID, totalItems: ITEMS.length },
        [`GET /api/jobs/${JOB_ID}`]: h.jobStatus({ total_items: ITEMS.length }),
        [`GET ${RESULTS}`]: previewServer(ITEMS),
        [`POST ${PROCESS}`]: { job_id: OTHER_JOB_ID, total_items: 3 },
        [`GET /api/jobs/${OTHER_JOB_ID}`]: h.jobStatus({ job_id: OTHER_JOB_ID, total_items: 3 }),
        ...extra
    };
}

function modalOpen(page) {
    return page.$('#processPreviewModal').classList.contains('active');
}

// The preview modal as plain data: each series with its files' rows.
function previewView(page) {
    return {
        title: page.$('#processPreviewModalTitle').textContent,
        summary: page.$('#processPreviewSummary').textContent,
        series: page.$$('#processPreviewList .process-preview-series').map(section => ({
            title: section.querySelector('.process-preview-series-title').textContent,
            files: [...section.querySelectorAll('.process-preview-item')].map(row => {
                const box = row.querySelector('input[type="checkbox"]');
                return {
                    path: row.dataset.filePath,
                    name: row.querySelector('.process-preview-name').textContent.replace(/\s+/g, ' ').trim(),
                    ticked: box ? box.checked : null,
                    tags: [...row.querySelectorAll('.process-preview-tags li')].map(li => li.textContent),
                    notes: [...row.querySelectorAll('.process-preview-note')].map(note => note.textContent),
                    clash: row.classList.contains('process-preview-item--clash'),
                    error: row.classList.contains('process-preview-item--error')
                };
            })
        })),
        empty: page.$('#processPreviewEmptyState').style.display === 'block' ? page.$('#processPreviewEmptyState').textContent : null,
        more: page.$('#processPreviewMoreBtn').style.display === 'none' ? null : page.$('#processPreviewMoreBtn').textContent,
        applyDisabled: page.$('#processPreviewApplyBtn').disabled
    };
}

function tickFile(page, filePath, checked) {
    const row = page.$$('#processPreviewList .process-preview-item').find(element => element.dataset.filePath === filePath);
    page.input(row.querySelector('input[type="checkbox"]'), checked);
}

// The preview job ends; the progress modal finishes and closes, so other jobs may start.
async function finishPreview(t, page, status = 'completed') {
    h.jobUpdate(page, status, h.progress(ITEMS.length, ITEMS.length));
    await page.settle();
    await h.tick(t, page, 500);
    await h.tick(t, page, 5000);
}

async function previewAll(t, page) {
    h.chooseMenuItem(page, 'processDropdown', 'Preview Process All');
    await page.settle();
    await finishPreview(t, page);
}

function clickButton(page, selector) {
    page.click(page.$(selector));
}

test('Preview Process All follows the preview job, then shows its results by series', async t => {
    const page = await h.bootJobsApp(t, { routes: routes() });

    h.chooseMenuItem(page, 'processDropdown', 'Preview Process All');
    await page.settle();

    const [start] = h.requests(page, 'POST', START);
    assert.deepEqual(start.body, { scope: 'all' });
    assert.equal(h.progressView(page).title, 'Previewing Changes...');
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Previewing changes to 6 files. Nothing is changed yet.' }]);
    assert.equal(modalOpen(page), false);

    await finishPreview(t, page);

    assert.equal(modalOpen(page), true);
    assert.deepEqual(previewView(page), {
        title: 'Preview changes: all files',
        summary: 'Checked 6 files: 2 renamed, 1 with tag changes, 2 name clashes, 1 that would fail, 1 unchanged.',
        series: [
            {
                title: 'Batman · 3 files',
                files: [
                    {
                        path: RENAMED.filePath,
                        name: 'batman 1.cbz → Batman - Chapter 0001.cbz',
                        ticked: true,
                        tags: ['Series: batman → Batman', 'VolumeYear: (empty) → 2016', 'Volume: 2016 → (removed)'],
                        notes: [],
                        clash: false,
                        error: false
                    },
                    {
                        path: TAKEN.filePath,
                        name: 'batman 2.cbz → Batman - Chapter 0002.cbz',
                        ticked: false,
                        tags: [],
                        notes: ['Batman - Chapter 0002.cbz already has this name, so processing would flag this file as a duplicate.'],
                        clash: true,
                        error: false
                    },
                    {
                        path: SECOND.filePath,
                        name: 'two.cbz → Batman - Chapter 0001.cbz',
                        ticked: false,
                        tags: [],
                        notes: ['batman 1.cbz would get this name first, so processing would flag this file as a duplicate.'],
                        clash: true,
                        error: false
                    }
                ]
            },
            {
                title: '(no series) · 1 file',
                files: [{
                    path: FAILING.filePath,
                    name: 'x.cbz',
                    ticked: false,
                    tags: [],
                    notes: ['Loose file in the library root'],
                    clash: false,
                    error: true
                }]
            },
            {
                title: 'Saga · 1 file',
                files: [{
                    path: UNWRITTEN.filePath,
                    name: 'saga 1.cbr → Saga - Chapter 0001.cbr',
                    ticked: true,
                    tags: [],
                    notes: ['Not normalized: this CBR/RAR/ZIP was left unchanged'],
                    clash: false,
                    error: false
                }]
            }
        ],
        empty: null,
        more: null,
        applyDisabled: false
    });
    assert.deepEqual(h.requests(page, 'GET', RESULTS).map(call => call.query), [{ filter: 'changes', offset: '0', limit: '200' }]);
});

test('Processing the ticked files starts a forced process-selected job for them', async t => {
    const page = await h.bootJobsApp(t, { routes: routes() });
    await previewAll(t, page);
    h.takeMessages(page);

    // Leave the renamed file alone, and process one the preview flagged.
    tickFile(page, RENAMED.filePath, false);
    tickFile(page, TAKEN.filePath, true);
    clickButton(page, '#processPreviewApplyBtn');
    await page.settle();

    assert.deepEqual(h.takeConfirms(page), ['Process 2 files now? They are renamed and their tags are written as the preview shows.']);
    const [call] = h.requests(page, 'POST', PROCESS);
    assert.deepEqual(call.body, { Files: [TAKEN.filePath, UNWRITTEN.filePath], ForceReprocess: true });
    assert.equal(modalOpen(page), false);
    assert.equal(h.progressView(page).title, 'Processing Previewed Files...');
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Started processing 2 files in background' }]);
    // Every file with changes was read for the ticks, not only the page shown.
    assert.deepEqual(h.requests(page, 'GET', RESULTS).at(-1).query, { filter: 'changes', offset: '0', limit: '1000' });
});

test('Processing reads every page of changes first, and one file is worded as one', async t => {
    const many = Array.from({ length: 250 }, (_, index) => item(`/w/S/${String(index).padStart(3, '0')}.cbz`, {
        series: 'S',
        newFilePath: `/w/S/S ${index}.cbz`,
        newFileName: `S ${index}.cbz`,
        renamed: true
    }));
    many[249] = { ...many[249], renamed: false, clash: 'existing', clashWith: '/w/S/S 0.cbz' };
    const page = await h.bootJobsApp(t, { routes: routes({ [`GET ${RESULTS}`]: previewServer(many, { pageSize: 200 }) }) });
    await previewAll(t, page);

    let view = previewView(page);
    assert.equal(view.summary, 'Checked 250 files: 249 renamed, 0 with tag changes, 1 name clash, 0 that would fail, 0 unchanged.');
    assert.equal(view.series[0].files.length, 200);
    assert.equal(view.more, 'Show more (50 left)');
    clickButton(page, '#processPreviewMoreBtn');
    await page.settle();
    view = previewView(page);
    assert.equal(view.series[0].title, 'S · 250 files');
    assert.equal(view.more, null);

    for (const entry of many.slice(1)) tickFile(page, entry.filePath, false);
    clickButton(page, '#processPreviewApplyBtn');
    await page.settle();

    assert.deepEqual(h.requests(page, 'GET', RESULTS).slice(-2).map(call => call.query.offset), ['0', '200']);
    assert.deepEqual(h.takeConfirms(page), ['Process 1 file now? They are renamed and their tags are written as the preview shows.']);
    assert.deepEqual(h.requests(page, 'POST', PROCESS)[0].body.Files, [many[0].filePath]);
});

test('Only changes can be turned off to list the files processing would leave alone', async t => {
    const page = await h.bootJobsApp(t, { routes: routes() });
    await previewAll(t, page);

    page.input(page.$('#processPreviewOnlyChanges'), false);
    await page.settle();

    const saga = previewView(page).series.at(-1);
    assert.equal(saga.title, 'Saga · 2 files');
    assert.deepEqual(saga.files[1], {
        path: UNCHANGED.filePath, name: 'Saga - Chapter 0002.cbz', ticked: null, tags: [], notes: [], clash: false, error: false
    });
    assert.equal(h.requests(page, 'GET', RESULTS).at(-1).query.filter, 'all');

    // A tick changed before switching is kept.
    tickFile(page, RENAMED.filePath, false);
    page.input(page.$('#processPreviewOnlyChanges'), true);
    await page.settle();
    assert.equal(previewView(page).series[0].files[0].ticked, false);
});

test('A preview with nothing to change says so and offers nothing to process', async t => {
    const nothing = [item('/w/S/a.cbz', { series: 'S', changed: false })];
    const page = await h.bootJobsApp(t, { routes: routes({ [`GET ${RESULTS}`]: previewServer(nothing) }) });
    await previewAll(t, page);

    let view = previewView(page);
    assert.equal(view.summary, 'Checked 1 file: 0 renamed, 0 with tag changes, 0 name clashes, 0 that would fail, 1 unchanged.');
    assert.equal(view.empty, 'Processing would change nothing in these files.');
    assert.equal(view.applyDisabled, true);

    // A preview stopped before it checked anything.
    page.route(`GET ${RESULTS}`, previewServer([], { status: 'cancelled' }));
    page.input(page.$('#processPreviewOnlyChanges'), false);
    await page.settle();
    view = previewView(page);
    assert.equal(view.empty, 'The preview has not checked any file.');
    assert.equal(view.summary, 'Checked 0 files: 0 renamed, 0 with tag changes, 0 name clashes, 0 that would fail, 0 unchanged. '
        + 'The preview stopped (cancelled) before it checked every file.');
});

test('Preview Process Selected previews the selected issues, and needs a selection', async t => {
    const page = await h.bootSeriesDetail(t, { routes: routes() });

    assert.equal(page.$('#previewSelectedItem').disabled, true);
    page.run('previewProcessSelected()');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Please select at least one file to preview' }]);

    h.selectIssue(page, ISSUE_A);
    assert.equal(page.$('#previewSelectedItem').disabled, false);
    h.chooseMenuItem(page, 'processDropdown', 'Preview Process Selected');
    await page.settle();
    assert.deepEqual(h.requests(page, 'POST', START)[0].body, { scope: 'selected', files: [ISSUE_A] });
    await finishPreview(t, page);
    assert.equal(previewView(page).title, 'Preview changes: 1 selected file');

    page.click(page.$('#processPreviewModal .close-btn'));
    h.selectIssue(page, ISSUE_A);
    h.selectIssue(page, ISSUE_B);
    h.chooseMenuItem(page, 'processDropdown', 'Preview Process Selected');
    await page.settle();
    await finishPreview(t, page);
    assert.equal(previewView(page).title, 'Preview changes: 2 selected files');
});

test('The series Actions menu previews the whole series', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const page = await bootSeriesPage({
        series: seriesSummary({ id: 's1', title: 'Batman' }),
        routes: { 'GET /api/active-job': { active: false }, ...routes() }
    });
    t.after(() => page.close());

    clickSeriesAction(page, 'Preview Changes');
    await page.settle();

    assert.deepEqual(h.requests(page, 'POST', START)[0].body, { scope: 'series', seriesId: 's1' });
    await finishPreview(t, page);
    assert.equal(previewView(page).title, 'Preview changes: Batman');
});

test('A preview of no files, or one the server refuses, starts nothing', async t => {
    const page = await h.bootJobsApp(t, {
        routes: routes({ [`POST ${START}`]: { job_id: '00000000-0000-0000-0000-000000000000', total_items: 0 } })
    });

    page.run('previewProcessAll()');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'No files to preview' }]);
    assert.equal(h.progressView(page).open, false);

    page.route(`POST ${START}`, respond(500, { error: 'boom' }));
    page.run('previewProcessAll()');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to start the preview: boom' }]);
    assert.equal(h.progressView(page).open, false);

    page.route(`POST ${START}`, 401);
    page.run('previewProcessAll()');
    await page.settle();
    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(h.takeMessages(page), []);
});

test('A one-file preview is worded as one, and no preview starts while another job runs', async t => {
    const page = await h.bootJobsApp(t, { routes: routes({ [`POST ${START}`]: { job_id: JOB_ID, total_items: 1 } }) });

    page.run('previewProcessAll()');
    await page.settle();
    assert.deepEqual(h.takeMessages(page), [{ type: 'info', text: 'Previewing changes to 1 file. Nothing is changed yet.' }]);

    page.run('previewSeriesProcessing("s1", "Batman")');
    await page.settle();
    assert.equal(h.requests(page, 'POST', START).length, 1);
    assert.match(h.takeMessages(page)[0].text, /Another job is already in progress/);
});

test('The results say when they cannot be loaded, and a late answer is dropped', async t => {
    const page = await h.bootJobsApp(t, { routes: routes({ [`GET ${RESULTS}`]: respond(500, { error: 'gone' }) }) });
    await previewAll(t, page);
    assert.equal(previewView(page).summary, 'Could not load the preview: gone');

    // An expired session goes to the login page.
    page.route(`GET ${RESULTS}`, 401);
    await page.run(`openProcessPreviewModal(${JSON.stringify(JOB_ID)}, 'again')`);
    assert.equal(h.loginRedirects(page), 1);
    assert.equal(previewView(page).summary, 'Loading the preview...');

    // Closed while loading: the answer, or its failure, changes nothing.
    const answer = deferred();
    page.route(`GET ${RESULTS}`, () => answer.promise);
    const opening = page.run(`openProcessPreviewModal(${JSON.stringify(JOB_ID)}, 'late')`);
    page.click(page.$('#processPreviewModal .modal-footer .btn:not(.btn-primary)'));
    assert.equal(modalOpen(page), false);
    answer.resolve(previewServer(ITEMS)({ query: { filter: 'changes', offset: '0', limit: '200' } }));
    await opening;
    assert.deepEqual(previewView(page).series, []);

    const failure = deferred();
    page.route(`GET ${RESULTS}`, () => failure.promise);
    const failing = page.run(`openProcessPreviewModal(${JSON.stringify(JOB_ID)}, 'late')`);
    page.run('closeProcessPreviewModal()');
    failure.resolve(respond(500, { error: 'late' }));
    await failing;
    assert.equal(previewView(page).summary, 'Loading the preview...');
});

test('Processing from the preview reports what stopped it', async t => {
    const page = await h.bootJobsApp(t, { routes: routes() });
    await previewAll(t, page);
    h.takeMessages(page);

    // Nothing ticked.
    for (const entry of [RENAMED, UNWRITTEN]) tickFile(page, entry.filePath, false);
    await page.run('applyProcessPreview()');
    assert.deepEqual(h.takeMessages(page), [{ type: 'warning', text: 'Tick at least one file to process' }]);
    tickFile(page, RENAMED.filePath, true);

    // The user thinks again.
    h.answerConfirms(page, false);
    await page.run('applyProcessPreview()');
    assert.equal(h.requests(page, 'POST', PROCESS).length, 0);
    assert.equal(modalOpen(page), true);

    // The changes cannot be read.
    page.route(`GET ${RESULTS}`, respond(500, { error: 'gone' }));
    await page.run('applyProcessPreview()');
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Could not read the preview: gone' }]);
    page.route(`GET ${RESULTS}`, 401);
    await page.run('applyProcessPreview()');
    assert.equal(h.loginRedirects(page), 1);
    assert.deepEqual(h.takeMessages(page), []);

    // The preview is closed while the changes are read.
    const answer = deferred();
    page.route(`GET ${RESULTS}`, () => answer.promise);
    const applying = page.run('applyProcessPreview()');
    page.run('closeProcessPreviewModal()');
    answer.resolve(previewServer(ITEMS)({ query: { filter: 'changes', offset: '0', limit: '1000' } }));
    await applying;
    assert.equal(h.requests(page, 'POST', PROCESS).length, 0);
});

test('A process job the server refuses is reported, and nothing starts while a start is pending', async t => {
    const page = await h.bootJobsApp(t, { routes: routes({ [`POST ${PROCESS}`]: respond(500, { error: 'boom' }) }) });
    await previewAll(t, page);
    h.takeMessages(page);

    await page.run('applyProcessPreview()');
    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: 'Failed to start processing: boom' }]);
    assert.equal(h.progressView(page).open, false);

    page.route(`POST ${PROCESS}`, 401);
    await page.run(`openProcessPreviewModal(${JSON.stringify(JOB_ID)}, 'again')`);
    await page.run('applyProcessPreview()');
    assert.equal(h.loginRedirects(page), 1);

    // That start is still pending (the page is on its way to the login
    // page), so no other job starts: nothing is read or started.
    await page.run(`openProcessPreviewModal(${JSON.stringify(JOB_ID)}, 'again')`);
    const reads = h.requests(page, 'GET', RESULTS).length;
    await page.run('applyProcessPreview()');
    assert.equal(h.requests(page, 'GET', RESULTS).length, reads);
    assert.equal(h.requests(page, 'POST', PROCESS).length, 2);
});

test('A changes list that ends early still processes what it read', async t => {
    const page = await h.bootJobsApp(t, { routes: routes() });
    await previewAll(t, page);

    // The server reports more changes than it sends (the list shrank meanwhile).
    page.route(`GET ${RESULTS}`, previewServer([RENAMED], { total: 5 }));
    await page.run('applyProcessPreview()');

    assert.deepEqual(h.requests(page, 'POST', PROCESS)[0].body.Files, [RENAMED.filePath]);
    assert.deepEqual(h.requests(page, 'GET', RESULTS).slice(-2).map(call => call.query.offset), ['0', '1']);
});
