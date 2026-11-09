# Webcomic Reader Design

The reader's webcomic mode shows a comic as one continuous scroll. It borrows its approach from
[Kavita](https://github.com/Kareadita/Kavita)'s infinite scroller. The code is in `wwwroot/js/pages/reader.js`.

## Loading pages

Only pages near the reader are loaded.

- **Buffer.** Pages within 5 places ahead of and behind the page on screen are fetched (`bufferPages`). The opening buffer
  loads in parallel, so a comic opens quickly at a saved page.
- **Visible page.** An `IntersectionObserver` with several thresholds finds the page most in view. It updates the current
  page, saves progress, and fetches more pages as new ones enter view.
- **Scroll direction.** The reader records whether you are moving forward or backward, using the scroll position.
- **Placement.** Images load independently and are placed in page order, whichever arrives first.

## Keeping memory bounded

A long session would otherwise keep every page it has ever shown. Once scrolling settles:

- Pages more than 15 places and more than 2 viewports from the one on screen become placeholders of the same height. Their
  image is released, and they are fetched again if you scroll back.
- Issues more than one behind the current issue are removed from the page, and the scroll position is adjusted so the view
  does not move.
- Switching mode releases everything.

## Reading across issues

When you reach the end of an issue, the next one is appended to the same stream with a transition marker. Its opening
pages are fetched ahead of time, so it starts without a pause. Each issue uses the same buffering.

## Controls

Tapping the stream shows or hides the controls, and scrolling leaves them as they are. In webcomic mode the bar along the
top shows your position.

Page turns in manga mode preload the pages either side of the one on screen (`MANGA_PRELOAD_AHEAD` and
`MANGA_PRELOAD_BEHIND`).

## Compared with Kavita

| Feature | Kavita | ComicMaintainer |
| --- | --- | --- |
| Buffered loading | Yes | Yes |
| Visibility tracking with `IntersectionObserver` | Several observers | One, with several thresholds |
| Scroll direction tracking | Yes | Yes |
| Visual debug mode | Yes | No |

## Testing changes

The page behaviour is covered by the jsdom tests in `tests/js`. Those cannot measure real scrolling, so also try:

- A comic of more than 50 pages, scrolling forward and back
- Reading through several issues, watching that memory stays level
- Switching between manga and webcomic modes
- Reopening a comic at a saved page
