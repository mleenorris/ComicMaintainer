'use strict';

// Press and hold to zoom (js/pages/reader.js): holding still on a page for a
// moment magnifies the spot held, following the press as it moves, until it
// ends; the release turns no page and leaves the controls as they are.

const test = require('node:test');
const assert = require('node:assert/strict');
const { openReader, openWebcomicReader, streamImage, FILE } = require('./support/reader-helpers');

const HOLD_MS = 350;
const chromeHidden = page => page.$('#readerContainer').classList.contains('chrome-hidden');
const pageImage = page => page.$('#readerContent .page-image');
const zoomed = image => image.classList.contains('hold-zoomed');
const origin = image => image.style.getPropertyValue('--hold-zoom-origin');
const currentPage = page => page.run('currentPage');
/** The page turned to `n` is shown (its picture replaced the last one). */
const shows = (page, n) => page.waitFor(() => pageImage(page) && pageImage(page).alt === `Page ${n}`);

/** A pointer event of a finger (pointer `id`) at (x, y), dispatched on `element`. */
function pointer(page, element, type, { x = 100, y = 200, id = 1, primary = true, button = 0 } = {}) {
    const event = new page.window.MouseEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y, button });
    Object.defineProperty(event, 'pointerId', { value: id });
    Object.defineProperty(event, 'isPrimary', { value: primary });
    Object.defineProperty(event, 'pointerType', { value: 'touch' });
    element.dispatchEvent(event);
    return event;
}

/** A finger lifted from `element` where it was: pointerup, then the click it makes. */
function release(page, element, at = {}) {
    pointer(page, element, 'pointerup', at);
    element.click();
}

const cancelable = (page, element, type) => {
    const event = new page.window.Event(type, { bubbles: true, cancelable: true });
    element.dispatchEvent(event);
    return event.defaultPrevented;
};

test('holding still on a page zooms in on the spot held, following the finger, until it lifts', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader();
    const zone = page.$('#tapZoneRight');
    const image = pageImage(page);

    assert.equal(cancelable(page, zone, 'contextmenu'), false, 'nothing held: the browser keeps its menu');
    assert.equal(cancelable(page, zone, 'touchmove'), false, 'and its scrolling');
    pointer(page, zone, 'pointermove');
    pointer(page, zone, 'pointerdown', { x: 300, y: 400 });
    assert.equal(cancelable(page, zone, 'contextmenu'), true, 'a long press opens no menu for the picture');
    t.mock.timers.tick(HOLD_MS - 1);
    assert.equal(zoomed(image), false, 'not before the hold');
    assert.equal(cancelable(page, zone, 'touchmove'), false, 'a finger not yet zoomed is the browser\'s');
    t.mock.timers.tick(1);
    assert.equal(zoomed(image), true);
    assert.equal(origin(image), '300px 400px');

    pointer(page, zone, 'pointermove', { x: 320, y: 380 });
    assert.equal(origin(image), '320px 380px', 'the zoom follows the finger');
    pointer(page, zone, 'pointermove', { x: 5, y: 5, id: 2 });
    assert.equal(origin(image), '320px 380px', 'another pointer moves nothing');
    assert.equal(cancelable(page, zone, 'touchmove'), true, 'nor does the finger scroll or pan the page');
    pointer(page, zone, 'pointerup', { id: 2 });
    assert.equal(zoomed(image), true, 'another pointer lifting ends nothing');

    release(page, zone, { x: 320, y: 380 });
    await page.settle();
    assert.equal(zoomed(image), false);
    assert.equal(origin(image), '320px 380px', 'it eases out where it was');
    assert.equal(currentPage(page), 1, 'the release turns no page');

    // The next tap turns the page as usual.
    pointer(page, zone, 'pointerdown');
    release(page, zone);
    await page.waitFor(() => currentPage(page) === 2);
});

test('a press that lifts or moves before the hold, or is not the main one, is a tap or a swipe', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader({ series: { [FILE]: 5 } });
    // Each page shown brings its own tap zones.
    const zone = () => page.$('#tapZoneRight');

    // Lifted before the hold: a tap.
    pointer(page, zone(), 'pointerdown');
    t.mock.timers.tick(HOLD_MS - 1);
    release(page, zone());
    t.mock.timers.tick(HOLD_MS);
    assert.equal(zoomed(pageImage(page)), false);
    await shows(page, 2);

    // A finger trembling within the slop still holds; one that moves on does not.
    pointer(page, zone(), 'pointerdown', { x: 100, y: 100 });
    pointer(page, zone(), 'pointermove', { x: 106, y: 106 });
    t.mock.timers.tick(HOLD_MS);
    assert.equal(zoomed(pageImage(page)), true, 'within the slop');
    pointer(page, zone(), 'pointercancel', { x: 106, y: 106 });
    assert.equal(zoomed(pageImage(page)), false, 'a cancelled press ends the zoom');

    pointer(page, zone(), 'pointerdown', { x: 100, y: 100 });
    pointer(page, zone(), 'pointermove', { x: 112, y: 100 });
    t.mock.timers.tick(HOLD_MS);
    assert.equal(zoomed(pageImage(page)), false, 'moved first');
    release(page, zone());
    await shows(page, 3);

    // A right button, or a finger that is not the first, holds nothing.
    for (const options of [{ button: 2 }, { primary: false }]) {
        pointer(page, zone(), 'pointerdown', options);
        t.mock.timers.tick(HOLD_MS);
        assert.equal(zoomed(pageImage(page)), false, JSON.stringify(options));
        assert.equal(page.run('holdZoom'), null);
    }
});

test('a second finger ends the zoom: that is a pinch', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader();
    const zone = page.$('#tapZoneLeft');

    pointer(page, zone, 'pointerdown');
    t.mock.timers.tick(HOLD_MS);
    assert.equal(zoomed(pageImage(page)), true);
    pointer(page, zone, 'pointerdown', { id: 2, primary: false });
    assert.equal(zoomed(pageImage(page)), false);
    assert.equal(page.run('holdZoom'), null);
    assert.equal(page.run('holdZoomSwallowsClick'), false, 'a pinch makes no click to swallow');
});

test('with no page shown yet a hold does nothing', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openReader();
    pageImage(page).remove();

    pointer(page, page.$('#tapZoneRight'), 'pointerdown');
    assert.equal(page.run('holdZoom'), null);
});

test('in webcomic mode a hold zooms the page of the stream held, and its release leaves the controls alone', async t => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const page = await openWebcomicReader({ series: { [FILE]: 3 } });
    const first = streamImage(page, FILE, 1);
    const second = streamImage(page, FILE, 2);
    const shown = chromeHidden(page);

    pointer(page, second, 'pointerdown', { x: 50, y: 60 });
    t.mock.timers.tick(HOLD_MS);
    assert.equal(zoomed(second), true);
    assert.equal(zoomed(first), false);
    release(page, second);
    assert.equal(zoomed(second), false);
    assert.equal(chromeHidden(page), shown, 'the release does not toggle the controls');

    // A page whose picture was released (far off screen) holds nothing.
    first.classList.add('webcomic-page-placeholder');
    pointer(page, first, 'pointerdown');
    assert.equal(page.run('holdZoom'), null);
});
