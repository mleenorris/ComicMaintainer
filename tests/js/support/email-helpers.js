'use strict';

// Shared fixtures and helpers for the email delivery (js/app/email.js) and
// per-file action (js/app/file-actions.js) tests. Response shapes follow
// EmailController, FilesController and the DTOs in ComicMaintainer.Core.

const { bootApp } = require('./app');

// EreaderDeviceDto (GET /api/email/devices → { devices: [...] }).
function emailDevice(overrides = {}) {
    return {
        id: 1,
        name: 'Kindle',
        emailAddress: 'reader@kindle.com',
        deliveryFormat: 'original',
        createdAt: '2024-01-02T03:04:05Z',
        updatedAt: '2024-02-03T04:05:06Z',
        ...overrides
    };
}

// GET /api/email/status.
function emailStatus(overrides = {}) {
    return { configured: true, from_address: 'comics@example.com', max_attachment_mb: 25, ...overrides };
}

// One book of POST /api/email/condense-plan.
function condensedBook(overrides = {}) {
    return {
        display_name: 'Saga #1-5',
        issue_count: 5,
        estimated_bytes: 10 * 1024 * 1024,
        exceeds_attachment_limit: false,
        ...overrides
    };
}

// POST /api/email/condense-plan.
function condensePlan(overrides = {}) {
    const books = overrides.books || [condensedBook()];
    const oversized = books.filter(book => book.exceeds_attachment_limit).length;
    return {
        mode: 'count',
        total_issues: books.reduce((sum, book) => sum + book.issue_count, 0),
        issues_per_book: 5,
        max_attachment_bytes: 25 * 1024 * 1024,
        can_email: books.length > 0 && oversized === 0,
        oversized_book_count: oversized,
        books,
        skipped: {},
        ...overrides
    };
}

// EmailController.ToBuildResponse.
function condenseBuild(overrides = {}) {
    const { progress, ...rest } = overrides;
    return {
        build_id: 'build-1',
        status: 'running',
        display_name: 'Saga #1-5',
        issue_count: 5,
        created_at: '2024-03-01T10:00:00Z',
        started_at: '2024-03-01T10:00:01Z',
        completed_at: null,
        error: null,
        file_size_bytes: null,
        expires_at: null,
        delivery_format: 'epub',
        progress: {
            phase: 'building',
            completed_pages: 0,
            total_pages: 0,
            completed_issues: 0,
            total_issues: 5,
            current_issue: null,
            pass: 1,
            total_passes: 1,
            percentage: 0,
            ...progress
        },
        ...rest
    };
}

// ComicEmailDeliveryDto (GET /api/email/deliveries → { deliveries: [...] }).
function emailDelivery(overrides = {}) {
    return {
        id: 1,
        filePath: 'Saga/Saga 001.cbz',
        fileName: 'Saga 001.cbz',
        deviceId: 1,
        deviceName: 'Kindle',
        deviceEmail: 'reader@kindle.com',
        deliveryFormat: 'epub',
        status: 'sent',
        source: 'manual',
        errorMessage: null,
        createdAt: '2024-03-01T10:00:00Z',
        sentAt: '2024-03-01T10:01:00Z',
        issueCount: 1,
        ...overrides
    };
}

// SeriesEmailSubscriptionDto (GET /api/email/subscriptions → { subscriptions: [...] }).
function emailSubscription(overrides = {}) {
    return {
        id: 7,
        normalizedSeriesKey: 'saga',
        seriesTitle: 'Saga',
        deviceId: 1,
        deviceName: 'Kindle',
        deviceEmail: 'reader@kindle.com',
        deliveryFormat: 'epub',
        enabled: true,
        lastSentAt: null,
        ...overrides
    };
}

// A series card of GET /api/files/series.
function seriesSummary(overrides = {}) {
    return {
        id: 'saga',
        title: 'Saga',
        canonical_title: 'Saga',
        aliases: [],
        issue_count: 2,
        total_size: 3 * 1024 * 1024,
        cover_file_path: 'Saga/Saga 001.cbz',
        ...overrides
    };
}

// An issue of GET /api/files/series/{id}/issues.
function seriesIssue(overrides = {}) {
    return {
        file_path: 'Saga/Saga 001.cbz',
        file_name: 'Saga 001.cbz',
        title: 'Saga 001',
        issue: '1',
        year: 2012,
        size: 1024 * 1024,
        read: false,
        duplicate: false,
        ...overrides
    };
}

/**
 * bootApp() plus the stylesheet rule that hides closed modals.
 *
 * The harness loads no CSS, so every .modal would otherwise count as visible
 * and modals.js would track all of them as open from the start; Escape would
 * then close whichever modal is last in the page instead of the one on top.
 * This is the `.modal` / `.modal.active` rule from css/main.css.
 */
function bootPage({ beforeScripts, routes = {}, ...options } = {}) {
    return bootApp({
        ...options,
        // An empty library, so the boot shows no "Failed to load series" toast.
        routes: { 'GET /api/files/series': { series: [], total_series: 0 }, ...routes },
        beforeScripts(window, page) {
            const style = window.document.createElement('style');
            style.textContent = '.modal { display: none; } .modal.active { display: flex; }';
            window.document.head.appendChild(style);
            if (beforeScripts) beforeScripts(window, page);
        }
    });
}

/**
 * Boots the SPA on a series page (#/series/{id}) whose issue grid lists
 * `issues`, so the per-issue and per-series action menus can be used.
 */
async function bootSeriesPage({ series = seriesSummary(), issues = [seriesIssue()], routes = {}, ...options } = {}) {
    return bootPage({
        hash: `#/series/${encodeURIComponent(series.id)}`,
        ...options,
        routes: {
            'GET /api/files/series': { series: [series], total_series: 1 },
            [`GET /api/files/series/${encodeURIComponent(series.id)}/issues`]: {
                id: series.id,
                title: series.title,
                issues,
                issue_count: issues.length,
                total_pages: 1,
                per_page: issues.length
            },
            ...routes
        }
    });
}

/** The toast messages currently shown, as { text, type }. */
function messages(page) {
    return page.$$('#messageContainer .message').map(element => ({
        text: element.textContent,
        type: [...element.classList].find(name => name !== 'message')
    }));
}

/** Texts of the toast messages of one type. */
function messageTexts(page, type) {
    return messages(page).filter(message => message.type === type).map(message => message.text);
}

function isModalOpen(page, id) {
    return page.$(`#${id}`).classList.contains('active');
}

/** Presses Escape the way a keyboard user would (modals.js closes the topmost modal). */
async function pressEscape(page) {
    // The open-modal stack is maintained by a MutationObserver; let it run first.
    await page.settle(2);
    page.document.dispatchEvent(new page.window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await page.settle(2);
}

function resolve(page, target) {
    const element = typeof target === 'string' ? page.$(target) : target;
    if (!element) throw new Error(`No element: ${target}`);
    return element;
}

/**
 * Makes the inline on* attributes of `element` and its ancestors live.
 *
 * The app renders through renderHtml(), which clones <template> content.
 * jsdom only turns an on* attribute into a handler when the attribute is set
 * on a node of a scripted document, so cloned nodes keep inert handlers
 * (browsers compile them lazily and do run them). Re-setting each attribute
 * once the node is in the page makes jsdom compile it; doing it again is a
 * no-op, so this is safe on markup from index.html too.
 */
function activateInlineHandlers(element) {
    for (let node = element; node && node.nodeType === 1; node = node.parentElement) {
        for (const { name, value } of [...node.attributes]) {
            if (name.startsWith('on')) node.setAttribute(name, value);
        }
    }
    return element;
}

/** page.click() for rendered markup: runs the inline handlers a browser would. */
function clickUi(page, target) {
    return page.click(activateInlineHandlers(resolve(page, target)));
}

/** page.input() for rendered markup (runs inline oninput/onchange handlers). */
function inputUi(page, target, value) {
    return page.input(activateInlineHandlers(resolve(page, target)), value);
}

/** Dispatches a keydown for `key` on `target`, as a key press would. */
function keydownUi(page, target, key) {
    const element = activateInlineHandlers(resolve(page, target));
    const event = new page.window.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    element.dispatchEvent(event);
    return event;
}

/** Clicks the item of an issue's action menu on a series page. */
function clickIssueAction(page, filePath, label) {
    const card = page.$$('.series-issue-card').find(element => element.dataset.filePath === filePath);
    if (!card) throw new Error(`No issue card for ${filePath}`);
    const item = [...card.querySelectorAll('.dropdown-item')].find(button => button.textContent.trim() === label);
    if (!item) throw new Error(`No "${label}" action for ${filePath}`);
    clickUi(page, card.querySelector('.dropdown-toggle'));
    return clickUi(page, item);
}

/** Clicks an item of the series page's Actions menu. */
function clickSeriesAction(page, label) {
    const menu = page.$('.series-actions-dropdown');
    if (!menu) throw new Error('No series actions menu');
    const item = [...menu.querySelectorAll('.dropdown-item')].find(button => button.textContent.trim() === label);
    if (!item) throw new Error(`No "${label}" series action`);
    clickUi(page, menu.querySelector('.dropdown-toggle'));
    return clickUi(page, item);
}

/** Finds a button by its visible text inside `root` (an element or a selector). */
function buttonByText(page, root, text) {
    const container = typeof root === 'string' ? page.$(root) : root;
    const button = [...container.querySelectorAll('button')].find(element => element.textContent.trim() === text);
    if (!button) throw new Error(`No "${text}" button in ${typeof root === 'string' ? root : root.id || root.className}`);
    return button;
}

/**
 * Replaces window.confirm with one that records the prompt (like the
 * harness's) and answers with the next of `answers` (the last one repeats).
 */
function answerConfirms(page, ...answers) {
    const queue = [...answers];
    page.window.confirm = message => {
        page.dialogs.push({ kind: 'confirm', message });
        return queue.length > 1 ? queue.shift() : queue[0];
    };
}

/** The URL-safe base64 path segment encodeFilePathForUrl (api.js) produces. */
function encodedPath(filePath) {
    return Buffer.from(filePath, 'utf8').toString('base64url');
}

/** Fetch calls made to `method path` (query ignored). */
function callsTo(page, method, path) {
    return page.fetchCalls.filter(call => call.method === method && call.path === path);
}

module.exports = {
    emailDevice,
    emailStatus,
    condensedBook,
    condensePlan,
    condenseBuild,
    emailDelivery,
    emailSubscription,
    seriesSummary,
    seriesIssue,
    bootPage,
    bootSeriesPage,
    messages,
    messageTexts,
    isModalOpen,
    pressEscape,
    activateInlineHandlers,
    clickUi,
    inputUi,
    keydownUi,
    clickIssueAction,
    clickSeriesAction,
    buttonByText,
    answerConfirms,
    encodedPath,
    callsTo
};
