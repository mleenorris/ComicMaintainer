# Mobile Layout Fix - File List Width Issue

## Problem
On mobile devices, the file list was extending beyond the header width, causing horizontal scrolling and a poor user experience. Long filenames were not being properly truncated, causing the grid layout to expand beyond the viewport width.

## Root Cause
The issue was caused by CSS Grid items not properly constraining their content width. Specifically:

1. **Missing overflow constraints**: No `overflow-x: hidden` on container elements
2. **Default `min-width: auto` on grid items**: CSS Grid items by default have `min-width: auto`, which prevents them from shrinking below their content size
3. **No explicit max-width constraints**: The `.container` and `.file-list` had no maximum width constraints on mobile

## Solution
Applied multiple CSS fixes in the mobile media queries (`@media (max-width: 768px)` and `@media (max-width: 480px)`):

### 1. Global Overflow Prevention
```css
html {
    overflow-x: hidden;
}

body {
    overflow-x: hidden;
}
```

### 2. Container Width Constraints
```css
.container {
    max-width: 100vw;
    overflow-x: hidden;
}

.file-list {
    overflow-x: hidden;
    max-width: 100%;
}

.actions-bar {
    overflow-x: hidden;
    max-width: 100%;
}
```

### 3. Grid Item Overflow
```css
.file-list-header,
.file-item,
.directory-header,
.directory-header-clickable {
    overflow: hidden;
}
```

### 4. Text Truncation (Most Important)
The key fix that enables proper text ellipsis in grid layouts:

```css
.file-name,
.file-path,
.directory-path {
    min-width: 0;  /* Critical: allows grid items to shrink */
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}
```

**Why `min-width: 0` is crucial:**
- By default, CSS Grid items have `min-width: auto`
- This means they won't shrink below their content width
- Setting `min-width: 0` allows the text truncation to work properly
- Without this, the ellipsis (`text-overflow: ellipsis`) won't activate

## Testing
To verify the fix works:

1. Open the web interface on a mobile device or in mobile view (DevTools)
2. Navigate to a folder with very long filenames (e.g., "Artifact Devouring Player - Chapter 0000.cbz")
3. Verify:
   - No horizontal scrolling occurs
   - The file list stays within the header width
   - Long filenames are truncated with ellipsis (...)
   - The layout remains responsive at all mobile widths (320px to 768px)

## Browser Compatibility
These CSS properties are well-supported across all modern browsers:
- `overflow-x: hidden` - ✅ All browsers
- `min-width: 0` - ✅ All browsers with CSS Grid support
- `text-overflow: ellipsis` - ✅ All browsers
- `max-width: 100vw` - ✅ All browsers

## References
- CSS Grid `min-width: auto` default: https://www.w3.org/TR/css-grid-1/#min-size-auto
- CSS `text-overflow`: https://developer.mozilla.org/en-US/docs/Web/CSS/text-overflow
- Responsive design best practices: https://web.dev/responsive-web-design-basics/
