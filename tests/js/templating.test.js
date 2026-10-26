'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { loadScripts } = require('./support/sandbox');
const { loadPage } = require('./support/dom');

function load() {
    return loadScripts(['js/shared.js', 'js/app/templating.js']);
}

// A real (jsdom) document with only the two scripts, for the DOM helpers.
async function loadDocument() {
    const page = loadPage({
        markup: '<!doctype html><html><body><div id="target"><p>old</p></div></body></html>',
        scripts: ['js/shared.js', 'js/app/templating.js']
    });
    await page.settle();
    return page;
}

// Unwrap an html``/rawHtml marker (created inside the sandbox) to its markup.
const markup = (sandbox, value) => sandbox.run('htmlToString')(value);

test('escapeHtml escapes every markup-significant character', () => {
    const { run } = load();
    const escapeHtml = run('escapeHtml');
    assert.equal(escapeHtml(`<a href="x" title='y'>&</a>`),
        '&lt;a href=&quot;x&quot; title=&#39;y&#39;&gt;&amp;&lt;/a&gt;');
});

test('escapeHtml stringifies values and treats null/undefined as empty', () => {
    const { run } = load();
    const escapeHtml = run('escapeHtml');
    assert.equal(escapeHtml(null), '');
    assert.equal(escapeHtml(undefined), '');
    assert.equal(escapeHtml(0), '0');
    assert.equal(escapeHtml(false), 'false');
    assert.equal(escapeHtml('plain text'), 'plain text');
});

test('html`` escapes interpolated values', () => {
    const sandbox = load();
    const result = sandbox.run('html`<span title="${"a\\"b"}">${"<script>alert(1)</script>"}</span>`');
    assert.equal(markup(sandbox, result),
        '<span title="a&quot;b">&lt;script&gt;alert(1)&lt;/script&gt;</span>');
});

test('html`` leaves the literal template parts untouched', () => {
    const sandbox = load();
    const result = sandbox.run('html`<div class="card" data-x=\'1\'>&nbsp;</div>`');
    assert.equal(markup(sandbox, result), `<div class="card" data-x='1'>&nbsp;</div>`);
});

test('rawHtml passes trusted markup through verbatim', () => {
    const sandbox = load();
    const result = sandbox.run('html`<p>${rawHtml("<b>bold</b> & more")}</p>`');
    assert.equal(markup(sandbox, result), '<p><b>bold</b> & more</p>');
    assert.equal(sandbox.run('rawHtml(null).value'), '');
});

test('nested html`` is not escaped twice', () => {
    const sandbox = load();
    const result = sandbox.run('const inner = html`<i>${"a & b"}</i>`; html`<p>${inner}</p>`');
    assert.equal(markup(sandbox, result), '<p><i>a &amp; b</i></p>');
});

test('html`` drops null/undefined/booleans and flattens arrays', () => {
    const sandbox = load();
    const result = sandbox.run(
        'html`<ul>${[1, "<2>", null].map(v => v === null ? null : html`<li>${v}</li>`)}${false}${undefined}${true}${0}</ul>`');
    assert.equal(markup(sandbox, result), '<ul><li>1</li><li>&lt;2&gt;</li>0</ul>');
});

test('uiIcon is trusted markup that also stringifies to the same markup', () => {
    const sandbox = load();
    const expected = '<svg class="icon" aria-hidden="true"><use href="#i-book-open"/></svg>';
    assert.equal(markup(sandbox, sandbox.run('html`${uiIcon("book-open")} Read`')), `${expected} Read`);
    assert.equal(sandbox.run('`${uiIcon("book-open")}`'), expected);
});

test('jsArg escapes for a single-quoted JS string inside a double-quoted attribute', () => {
    const sandbox = load();
    const jsArgMarkup = value => markup(sandbox, sandbox.run('jsArg')(value));

    assert.equal(jsArgMarkup(`it's`), `it\\'s`);
    assert.equal(jsArgMarkup('say "hi"'), 'say &quot;hi&quot;');
    assert.equal(jsArgMarkup('C:\\comics'), 'C:\\\\comics');
    assert.equal(jsArgMarkup('a&b'), 'a&amp;b');
    assert.equal(jsArgMarkup('line1\nline2\r\tend'), 'line1\\nline2\\r\\tend');
    assert.equal(jsArgMarkup(null), '');
    assert.equal(jsArgMarkup(42), '42');
});

test('jsArg output round-trips through HTML attribute decoding and the JS parser', () => {
    const sandbox = load();
    const decodeAttribute = value => value
        .replace(/&quot;/g, '"')
        .replace(/&#39;/g, "'")
        .replace(/&amp;/g, '&');
    const samples = [
        `Hazure Zokusei "Hikari Mahou" ga`,
        `O'Brien's \\ path`,
        '&#39;); alert(1); //',
        '/comics/Series (2020)/Issue #1.cbz',
        'tab\tnew\nline'
    ];
    for (const sample of samples) {
        const attribute = markup(sandbox, sandbox.run('html')(['<button onclick="f(\'', '\')">'], sandbox.run('jsArg')(sample)));
        const handler = /onclick="([^"]*)"/.exec(attribute)[1];
        let received;
        vm.runInNewContext(decodeAttribute(handler), { f: value => { received = value; } });
        assert.equal(received, sample);
    }
});

test('getParsedTemplate caches parsed templates per markup string', () => {
    const sandbox = load();
    const first = sandbox.run('getParsedTemplate("<p>a</p>")');
    const second = sandbox.run('getParsedTemplate("<p>a</p>")');
    assert.equal(first, second);
    assert.equal(first.innerHTML, '<p>a</p>');
    assert.equal(sandbox.createdElements.length, 1);
    assert.equal(sandbox.createdElements[0].tagName, 'TEMPLATE');
});

test('getParsedTemplate evicts the least recently used entry when full', () => {
    const sandbox = load();
    const max = sandbox.run('MAX_TEMPLATE_CACHE_ENTRIES');
    for (let i = 0; i < max; i++) sandbox.run(`getParsedTemplate("<i>${i}</i>")`);
    sandbox.run('getParsedTemplate("<i>0</i>")'); // touch: now most recent
    sandbox.run('getParsedTemplate("<i>new</i>")');
    assert.equal(sandbox.run('TEMPLATE_CACHE.size'), max);
    assert.equal(sandbox.run('TEMPLATE_CACHE.has("<i>0</i>")'), true);
    assert.equal(sandbox.run('TEMPLATE_CACHE.has("<i>1</i>")'), false);
});

test('renderHtml replaces, appendHtml adds and clearChildren empties a real element', async () => {
    const page = await loadDocument();
    const target = page.$('#target');

    assert.equal(page.run("renderHtml(document.getElementById('target'), html`<b>${'1 < 2'}</b>`)"), target);
    assert.equal(target.innerHTML, '<b>1 &lt; 2</b>');

    page.run("appendHtml(document.getElementById('target'), html`<i>more</i>`)");
    page.run("appendHtml(document.getElementById('target'), html`<i>more</i>`)");
    assert.equal(target.innerHTML, '<b>1 &lt; 2</b><i>more</i><i>more</i>');
    const [first, second] = target.querySelectorAll('i');
    assert.notEqual(first, second, 'each render clones the cached template');

    assert.equal(page.run("clearChildren(document.getElementById('target'))"), target);
    assert.equal(target.childNodes.length, 0);
});

test('a plain string is rendered as text, not parsed as markup', async () => {
    const page = await loadDocument();
    page.run("renderHtml(document.getElementById('target'), '<img src=x onerror=alert(1)>')");
    assert.equal(page.$('#target').textContent, '<img src=x onerror=alert(1)>');
    assert.equal(page.$('#target img'), null);
});

test('the DOM helpers do nothing when the target element is missing', async () => {
    const page = await loadDocument();
    for (const helper of ['renderHtml', 'appendHtml', 'clearChildren']) {
        assert.equal(page.run(`${helper}(document.getElementById('missing'), html\`<p>x</p>\`)`), null, helper);
        assert.equal(page.run(`${helper}(undefined, html\`<p>x</p>\`)`), undefined, helper);
    }
    assert.equal(page.$('#target').innerHTML, '<p>old</p>');
});

test('htmlToElement returns the first element of the markup, or null when there is none', async () => {
    const page = await loadDocument();
    const element = page.run("htmlToElement(html`text first <span class=\"a\">${'x'}</span><em>y</em>`)");
    assert.equal(element.tagName, 'SPAN');
    assert.equal(element.outerHTML, '<span class="a">x</span>');
    assert.equal(page.run('htmlToElement(html`only text`)'), null);
});
