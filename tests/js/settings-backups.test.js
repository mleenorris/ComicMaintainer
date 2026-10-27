'use strict';

// Settings → Backups (js/app/settings.js): listing backups, creating,
// downloading, deleting and restoring them (from the list or an uploaded
// file), cancelling a staged restore, and the administrator-only gating.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { deferred } = require('./support/app');
const {
    MB, bootSettings, messages, clearMessages, callsTo, requestLog, answerConfirm, clickRendered
} = require('./support/settings-helpers');

const NAME = 'comicmaintainer-20250301-120000.db';
const ODD = "odd 'name'.db";
const BACKUPS = [
    { name: NAME, size_bytes: 5.5 * MB, created_utc: '2025-03-01T12:00:00Z' },
    { name: ODD, size_bytes: 0, created_utc: 'not a date' },
    { name: 'empty.db', size_bytes: null, created_utc: null }
];
const STAGED = {
    source: NAME,
    safety_backup: { name: 'pre-restore-20250302-080000.db', size_bytes: MB, created_utc: '2025-03-02T08:00:00Z' },
    restart_required: true,
    message: 'Restore staged. Restart ComicMaintainer to apply it.'
};
const networkError = () => {
    throw new TypeError('Failed to fetch');
};

const listView = page => {
    const items = page.$$('#backupsList .backups-item');
    if (!items.length) return page.$('#backupsList').textContent.trim();
    return items.map(item => [
        item.querySelector('.backups-item-name').textContent,
        item.querySelector('.backups-item-meta').textContent,
        page.$$(`#backupsList .backups-item:nth-child(${items.indexOf(item) + 1}) button`)
            .map(button => `${button.getAttribute('aria-label')} [${button.textContent.trim()}]`)
    ]);
};

const visibility = page => ({
    section: !page.$('#settings-backups').hidden,
    nav: !page.$('#settingsNavBackups').hidden,
    pending: !page.$('#backupsRestorePending').hidden
});

const button = (page, label) => page.$(`#backupsList button[aria-label="${label}"]`);

function bootBackups(routes = {}, options = {}) {
    return bootSettings({ ...options, routes: { 'GET /api/backups': { backups: BACKUPS, restore_pending: false }, ...routes } });
}

/** Run an action and let the list reload that follows it finish. */
async function act(page, action) {
    clearMessages(page);
    const since = page.fetchCalls.length;
    if (typeof action === 'string') await page.run(action);
    else action();
    await page.settle();
    return requestLog(page, since);
}

test('the Backups section lists each backup with its size, date and actions', async () => {
    const page = await bootBackups();

    const [call] = callsTo(page, 'GET /api/backups');
    assert.equal(call.init.credentials, 'include');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.deepEqual(visibility(page), { section: true, nav: true, pending: false });
    const created = page.evaluate(`new Date('2025-03-01T12:00:00Z').toLocaleString()`);
    assert.deepEqual(listView(page), [
        [NAME, `5.5 MB · ${created}`, [`Download ${NAME} [Download]`, `Restore ${NAME} [Restore]`, `Delete ${NAME} [Delete]`]],
        [ODD, '0 B · —', [`Download ${ODD} [Download]`, `Restore ${ODD} [Restore]`, `Delete ${ODD} [Delete]`]],
        ['empty.db', '0 B · —', ['Download empty.db [Download]', 'Restore empty.db [Restore]', 'Delete empty.db [Delete]']]
    ]);
});

test('the backups list shows loading, empty, pending-restore and error states', async () => {
    const page = await bootBackups({ 'GET /api/backups': { backups: [], restore_pending: true } });
    assert.equal(listView(page), 'No backups yet.');
    assert.deepEqual(visibility(page), { section: true, nav: true, pending: true });

    const pending = deferred();
    page.route('GET /api/backups', () => pending.promise);
    const loading = page.run('loadBackups()');
    await page.settle();
    assert.equal(listView(page), 'Loading backups...');
    pending.resolve({ backups: 'not a list', restore_pending: false });
    await loading;
    assert.equal(listView(page), 'No backups yet.');
    assert.equal(visibility(page).pending, false);

    for (const [failure, message] of [
        [respond(500, { error: 'Backup directory is not readable' }), 'Backups could not be loaded: Backup directory is not readable'],
        [respond(500, { message: 'Storage offline' }), 'Backups could not be loaded: Storage offline'],
        [respond(500, {}), 'Backups could not be loaded: HTTP error! status: 500'],
        [respond(502, '<html>Bad gateway</html>'), 'Backups could not be loaded: HTTP error! status: 502'],
        [networkError, 'Backups could not be loaded: Failed to fetch']
    ]) {
        page.route('GET /api/backups', failure);
        await page.run('loadBackups()');
        assert.equal(listView(page), message);
        assert.deepEqual(messages(page), [], 'list failures are shown in place, not as a toast');
    }
    assert.equal(page.consoleMessages.filter(m => m.type === 'error' && m.args[0] === 'Failed to load backups:').length, 5);

    // An expired session redirects to the login page without an error.
    page.route('GET /api/backups', 401);
    await page.run('loadBackups()');
    assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
    assert.equal(listView(page), 'Loading backups...');
    assert.deepEqual(visibility(page), { section: true, nav: true, pending: false });
});

test('only the newest backups list request is rendered', async () => {
    const page = await bootBackups();

    // An older success arriving late is ignored...
    const slow = deferred();
    page.route('GET /api/backups', () => slow.promise);
    const older = page.run('loadBackups()');
    await page.settle();
    page.route('GET /api/backups', { backups: [], restore_pending: true });
    await page.run('loadBackups()');
    slow.resolve({ backups: BACKUPS, restore_pending: false });
    await older;
    assert.equal(listView(page), 'No backups yet.');
    assert.equal(visibility(page).pending, true);

    // ...and so is an older failure.
    const failing = deferred();
    page.route('GET /api/backups', () => failing.promise);
    const olderFailure = page.run('loadBackups()');
    await page.settle();
    page.route('GET /api/backups', { backups: BACKUPS.slice(0, 1), restore_pending: false });
    await page.run('loadBackups()');
    failing.resolve(respond(500, { error: 'too late' }));
    await olderFailure;
    assert.equal(listView(page).length, 1);
    assert.equal(page.consoleMessages.filter(m => m.args[0] === 'Failed to load backups:').length, 0);

    // A list or banner removed from the page mid-load is skipped quietly.
    const late = deferred();
    page.route('GET /api/backups', () => late.promise);
    const loading = page.run('loadBackups()');
    await page.settle();
    page.$('#backupsList').remove();
    page.$('#backupsRestorePending').remove();
    late.resolve({ backups: BACKUPS, restore_pending: true });
    await loading;
    const since = page.fetchCalls.length;
    await page.run('loadBackups()');
    assert.deepEqual(requestLog(page, since), [], 'nothing is fetched without a list to show');
    assert.deepEqual(page.errors, []);
});

test('Backups is hidden from non-administrators and after a 403', async () => {
    const reader = await bootBackups({ 'GET /api/backups': 403 }, { user: { canModifyLibrary: false, canAdminister: false } });
    assert.deepEqual(visibility(reader), { section: false, nav: false, pending: false });
    const since = reader.fetchCalls.length;
    await reader.run('loadBackups()');
    assert.deepEqual(requestLog(reader, since), []);

    // An administrator token the server still rejects hides the section too,
    // and it stays hidden without asking again.
    const page = await bootBackups({ 'GET /api/backups': 403 });
    assert.deepEqual(visibility(page), { section: false, nav: false, pending: false });
    assert.deepEqual(messages(page), []);
    const after = page.fetchCalls.length;
    await page.run('loadBackups()');
    assert.deepEqual(requestLog(page, after), []);
});

test('Create backup now creates a backup and refreshes the list', async () => {
    const page = await bootBackups();
    const created = { name: 'comicmaintainer-20250302-080000.db', size_bytes: 6 * MB, created_utc: '2025-03-02T08:00:00Z' };
    const pending = deferred();
    page.route('POST /api/backups', () => pending.promise);
    page.route('GET /api/backups', { backups: [created, ...BACKUPS], restore_pending: false });

    clearMessages(page);
    const since = page.fetchCalls.length;
    page.click('#createBackupBtn');
    await page.settle(2);
    assert.equal(page.$('#createBackupBtn').disabled, true);
    assert.deepEqual(messages(page), [{ type: 'info', text: 'Creating backup...' }]);
    const [call] = callsTo(page, 'POST /api/backups', since);
    assert.equal(call.init.credentials, 'include');
    assert.match(call.headers.authorization, /^Bearer /);

    pending.resolve(created);
    await page.settle();
    assert.equal(page.$('#createBackupBtn').disabled, false);
    assert.deepEqual(requestLog(page, since), ['POST /api/backups', 'GET /api/backups']);
    assert.deepEqual(messages(page), [
        { type: 'info', text: 'Creating backup...' },
        { type: 'success', text: 'Backup comicmaintainer-20250302-080000.db created.' }
    ]);
    assert.equal(listView(page)[0][0], created.name);
});

test('a failed backup is reported with its correlation reference', async () => {
    const page = await bootBackups();

    page.route('POST /api/backups', respond(409, { error: 'A backup is already running' }, { 'X-Correlation-Id': 'corr-409' }));
    assert.deepEqual(await act(page, 'createBackup()'), ['POST /api/backups', 'GET /api/backups']);
    assert.deepEqual(messages(page), [
        { type: 'info', text: 'Creating backup...' },
        { type: 'error', text: 'Failed to create backup: A backup is already running (ref: corr-409)' }
    ]);
    assert.equal(page.$('#createBackupBtn').disabled, false);

    page.run('lastApiFailure = null');
    for (const [failure, text] of [
        [respond(500, { message: 'Disk full' }), 'Failed to create backup: Disk full'],
        [respond(500, 'Internal Server Error'), 'Failed to create backup: HTTP error! status: 500'],
        [networkError, 'Failed to create backup: Failed to fetch']
    ]) {
        page.route('POST /api/backups', failure);
        await act(page, 'createBackup()');
        assert.deepEqual(messages(page).at(-1), { type: 'error', text });
    }

    // Without the button the backup is still created.
    page.route('POST /api/backups', { name: 'no-button.db' });
    page.$('#createBackupBtn').remove();
    await act(page, 'createBackup()');
    assert.deepEqual(messages(page).at(-1), { type: 'success', text: 'Backup no-button.db created.' });
});

test('Download fetches the backup with the session and saves it under its name', async t => {
    const page = await bootBackups();
    t.mock.timers.enable({ apis: ['setTimeout'] });
    page.route(`GET /api/backups/${NAME}/download`, () =>
        new Response(Buffer.from('SQLite format 3\u0000'), { headers: { 'Content-Type': 'application/vnd.sqlite3' } }));

    const saved = [];
    page.document.addEventListener('click', event => {
        if (!event.target.matches('a[download]')) return;
        event.preventDefault();
        saved.push({ href: event.target.href, download: event.target.download, attached: event.target.isConnected });
    });

    assert.deepEqual(await act(page, () => clickRendered(page, button(page, `Download ${NAME}`))), [`GET /api/backups/${NAME}/download`]);
    const [call] = callsTo(page, `GET /api/backups/${NAME}/download`);
    assert.match(call.headers.authorization, /^Bearer /);
    assert.equal(saved.length, 1);
    assert.match(saved[0].href, /^blob:http:\/\/localhost\//);
    assert.deepEqual({ ...saved[0], href: undefined }, { href: undefined, download: NAME, attached: true });
    assert.equal(page.$('a[download]'), null, 'the temporary link is removed');
    assert.equal(await page.objectUrls.get(saved[0].href).text(), 'SQLite format 3\u0000');
    assert.deepEqual(messages(page), []);

    // The object URL outlives the click so the save is not cancelled.
    t.mock.timers.tick(59999);
    assert.equal(page.objectUrls.has(saved[0].href), true);
    t.mock.timers.tick(1);
    assert.equal(page.objectUrls.has(saved[0].href), false);

    // Names are passed to the API URL-encoded, quotes and all.
    page.route(`GET /api/backups/odd%20'name'.db/download`, respond(404, { error: 'Backup not found' }));
    assert.deepEqual(await act(page, () => clickRendered(page, button(page, `Download ${ODD}`))), [`GET /api/backups/odd%20'name'.db/download`]);
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Failed to download backup: Backup not found' }]);
    assert.equal(saved.length, 1);

    page.route(`GET /api/backups/${NAME}/download`, networkError);
    await act(page, () => clickRendered(page, button(page, `Download ${NAME}`)));
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Failed to download backup: Failed to fetch' }]);
    assert.deepEqual(page.errors, []);
});

test('Delete asks first and refreshes the list afterwards', async () => {
    const page = await bootBackups({ [`DELETE /api/backups/${NAME}`]: { deleted: NAME } });

    answerConfirm(page, false);
    assert.deepEqual(await act(page, () => clickRendered(page, button(page, `Delete ${NAME}`))), []);
    assert.deepEqual(page.dialogs.at(-1), { kind: 'confirm', message: `Delete backup ${NAME}?\n\nThis cannot be undone.` });

    answerConfirm(page, true);
    assert.deepEqual(await act(page, () => clickRendered(page, button(page, `Delete ${NAME}`))), [`DELETE /api/backups/${NAME}`, 'GET /api/backups']);
    assert.deepEqual(messages(page), [{ type: 'success', text: `Backup ${NAME} deleted.` }]);
    assert.equal(callsTo(page, `DELETE /api/backups/${NAME}`)[0].init.credentials, 'include');

    page.route(`DELETE /api/backups/odd%20'name'.db`, respond(404, { error: 'Backup not found' }));
    assert.deepEqual(await act(page, () => clickRendered(page, button(page, `Delete ${ODD}`))), [`DELETE /api/backups/odd%20'name'.db`, 'GET /api/backups']);
    assert.equal(page.dialogs.at(-1).message, `Delete backup ${ODD}?\n\nThis cannot be undone.`);
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Failed to delete backup: Backup not found' }]);
});

test('Restore stages a restore after confirmation and reports the safety backup', async () => {
    const page = await bootBackups({ [`POST /api/backups/${NAME}/restore`]: STAGED });
    const warning =
        `Restore the database from backup ${NAME}?\n\n` +
        'A safety backup of the current database is taken first. The restore is applied the next time ' +
        'ComicMaintainer restarts, replacing all current data (settings, users, read status and history).';

    answerConfirm(page, false);
    assert.deepEqual(await act(page, () => clickRendered(page, button(page, `Restore ${NAME}`))), []);
    assert.deepEqual(page.dialogs.at(-1), { kind: 'confirm', message: warning });
    assert.deepEqual(messages(page), []);

    answerConfirm(page, true);
    page.route('GET /api/backups', { backups: BACKUPS, restore_pending: true });
    assert.deepEqual(await act(page, () => clickRendered(page, button(page, `Restore ${NAME}`))), [`POST /api/backups/${NAME}/restore`, 'GET /api/backups']);
    assert.deepEqual(messages(page), [
        { type: 'info', text: 'Validating backup...' },
        { type: 'warning', text: 'Restore staged. Restart ComicMaintainer to apply it. Safety backup: pre-restore-20250302-080000.db.' }
    ]);
    assert.equal(visibility(page).pending, true);

    for (const [result, text] of [
        [{ message: 'Restore staged for the next start.' }, 'Restore staged for the next start.'],
        [{ safety_backup: { name: 'safety.db' } }, 'Restore staged. Restart ComicMaintainer to apply it. Safety backup: safety.db.'],
        [{ safety_backup: {} }, 'Restore staged. Restart ComicMaintainer to apply it.'],
        [respond(200, 'null'), 'Restore staged. Restart ComicMaintainer to apply it.']
    ]) {
        page.route(`POST /api/backups/${NAME}/restore`, result);
        await act(page, `restoreBackup('${NAME}')`);
        assert.deepEqual(messages(page).at(-1), { type: 'warning', text });
    }

    for (const [failure, text] of [
        [respond(400, { error: 'The file is not a ComicMaintainer database' }), 'Failed to restore backup: The file is not a ComicMaintainer database'],
        [respond(409, { error: 'A restore is already staged' }), 'Failed to restore backup: A restore is already staged'],
        [networkError, 'Failed to restore backup: Failed to fetch']
    ]) {
        page.route(`POST /api/backups/${NAME}/restore`, failure);
        assert.deepEqual(await act(page, `restoreBackup('${NAME}')`), [`POST /api/backups/${NAME}/restore`, 'GET /api/backups']);
        assert.deepEqual(messages(page), [{ type: 'info', text: 'Validating backup...' }, { type: 'error', text }]);
    }
});

test('Restore from file uploads a chosen .db file after confirmation', async () => {
    const page = await bootBackups({ 'POST /api/backups/upload-restore': STAGED });
    const input = page.$('#restoreBackupFileInput');
    const choose = file => {
        Object.defineProperty(input, 'files', { value: file ? [file] : [], configurable: true });
        input.dispatchEvent(new page.window.Event('change', { bubbles: true }));
    };
    const dbFile = new page.window.File(['SQLite format 3\u0000'], 'Library-Backup.DB', { type: 'application/octet-stream' });

    // The visible button opens the hidden file picker.
    let pickerOpened = 0;
    input.addEventListener('click', () => pickerOpened++);
    page.click('#restoreBackupFileBtn');
    assert.equal(pickerOpened, 1);

    assert.deepEqual(await act(page, () => choose(null)), [], 'cancelling the picker does nothing');
    assert.deepEqual(await act(page, () => page.run('restoreBackupFromFile(null)')), []);

    const dialogs = page.dialogs.length;
    assert.deepEqual(await act(page, () => choose(new page.window.File(['notes'], 'notes.txt'))), []);
    assert.deepEqual(messages(page), [{ type: 'error', text: 'Choose a ComicMaintainer database backup (.db file).' }]);
    assert.equal(page.dialogs.length, dialogs, 'no confirmation for the wrong kind of file');

    answerConfirm(page, false);
    assert.deepEqual(await act(page, () => choose(dbFile)), []);
    assert.match(page.dialogs.at(-1).message, /^Restore the database from the uploaded file Library-Backup\.DB\?\n\nA safety backup/);

    answerConfirm(page, true);
    assert.deepEqual(await act(page, () => choose(dbFile)), ['POST /api/backups/upload-restore', 'GET /api/backups']);
    const [call] = callsTo(page, 'POST /api/backups/upload-restore');
    assert.equal(call.body instanceof page.window.FormData, true);
    assert.equal(call.body.get('file').name, 'Library-Backup.DB');
    assert.equal(await call.body.get('file').text(), 'SQLite format 3\u0000');
    assert.equal(call.headers['content-type'], undefined, 'the browser sets the multipart boundary');
    assert.match(call.headers.authorization, /^Bearer /);
    assert.equal(call.init.credentials, 'include');
    assert.deepEqual(messages(page), [
        { type: 'info', text: 'Uploading and validating backup...' },
        { type: 'warning', text: 'Restore staged. Restart ComicMaintainer to apply it. Safety backup: pre-restore-20250302-080000.db.' }
    ]);

    for (const [failure, text] of [
        [respond(400, { error: 'File too large' }), 'Failed to restore from file: File too large'],
        [networkError, 'Failed to restore from file: Failed to fetch']
    ]) {
        page.route('POST /api/backups/upload-restore', failure);
        assert.deepEqual(await act(page, () => choose(dbFile)), ['POST /api/backups/upload-restore', 'GET /api/backups']);
        assert.deepEqual(messages(page), [{ type: 'info', text: 'Uploading and validating backup...' }, { type: 'error', text }]);
    }
});

test('a staged restore can be cancelled from the banner', async () => {
    const page = await bootBackups({
        'GET /api/backups': { backups: BACKUPS, restore_pending: true },
        'DELETE /api/backups/restore-pending': { cancelled: true }
    });
    assert.equal(visibility(page).pending, true);

    page.route('GET /api/backups', { backups: BACKUPS, restore_pending: false });
    assert.deepEqual(await act(page, () => page.click('#cancelPendingRestoreBtn')), ['DELETE /api/backups/restore-pending', 'GET /api/backups']);
    assert.deepEqual(messages(page), [{ type: 'success', text: 'Pending restore cancelled. The current database will be kept.' }]);
    assert.equal(visibility(page).pending, false);

    for (const [failure, text] of [
        [respond(404, { error: 'No restore is pending' }), 'Failed to cancel the pending restore: No restore is pending'],
        [networkError, 'Failed to cancel the pending restore: Failed to fetch']
    ]) {
        page.route('DELETE /api/backups/restore-pending', failure);
        await act(page, 'cancelPendingRestore()');
        assert.deepEqual(messages(page), [{ type: 'error', text }]);
    }
});

test('backup actions answered with 401 or 403 show no error toast', async () => {
    const page = await bootBackups();
    answerConfirm(page, true);
    const token = page.window.localStorage.getItem('jwt_token');
    const dbFile = new page.window.File(['db'], 'upload.db');
    const actions = [
        ['POST /api/backups', 'createBackup()'],
        [`GET /api/backups/${NAME}/download`, `downloadBackup('${NAME}')`],
        [`DELETE /api/backups/${NAME}`, `deleteBackup('${NAME}')`],
        [`POST /api/backups/${NAME}/restore`, `restoreBackup('${NAME}')`],
        ['POST /api/backups/upload-restore', () => page.run('restoreBackupFromFile')({ files: [dbFile], value: 'C:\\fakepath\\upload.db' })],
        ['DELETE /api/backups/restore-pending', 'cancelPendingRestore()']
    ];
    const errors = () => messages(page).filter(message => message.type === 'error');

    for (const [route, action] of actions) {
        page.window.localStorage.setItem('jwt_token', token);
        const redirects = page.navigations.length;
        page.route(route, 401);
        await act(page, action);
        assert.deepEqual(errors(), [], `401 ${route}`);
        assert.equal(page.navigations.length, redirects + 1, `401 ${route} redirects`);
        assert.equal(page.navigations.at(-1).url, 'http://localhost/login.html');
        assert.deepEqual(visibility(page), { section: true, nav: true, pending: false });
    }

    // A 403 hides the section; later actions skip the list reload entirely.
    page.window.localStorage.setItem('jwt_token', token);
    for (const [route, action] of actions) {
        page.route(route, respond(403, { error: 'Forbidden' }));
        const log = await act(page, action);
        assert.deepEqual(log, [`${route.split(' ')[0]} ${route.split(' ')[1]}`], `403 ${route}`);
        assert.deepEqual(errors(), [], `403 ${route}`);
        assert.deepEqual(visibility(page), { section: false, nav: false, pending: false });
    }
});
