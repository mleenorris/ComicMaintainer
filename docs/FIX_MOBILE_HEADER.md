# Mobile File List Header Fix

## Issue
The files top bar had its size cut off in mobile view due to a CSS grid column mismatch.

## Root Cause
In mobile view (max-width: 768px), the file list header had the following configuration:
- **HTML Structure**: 5 elements (Checkbox, Toggle Button, "File", "Size", "Actions")
- **CSS Visibility**: "Size" and "Actions" columns were hidden using `display: none`
- **Grid Definition**: Set to 4 columns (`25px 25px 1fr 70px`)

This mismatch created an unused 70px column that prevented the "File" column from using the full available horizontal space.

## Solution
Updated the `grid-template-columns` property in mobile responsive styles to match the number of visible elements:

### Changes Made
1. **Mobile view (max-width: 768px)**:
   - Before: `grid-template-columns: 25px 25px 1fr 70px;`
   - After: `grid-template-columns: 25px 25px 1fr;`

2. **Extra-small mobile view (max-width: 480px)**:
   - Before: `grid-template-columns: 20px 25px 1fr 70px;`
   - After: `grid-template-columns: 20px 25px 1fr;`

## Technical Details
The grid now correctly has 3 columns matching the 3 visible elements:
1. **Column 1 (25px/20px)**: Checkbox for file selection
2. **Column 2 (25px)**: Expand/collapse toggle button
3. **Column 3 (1fr)**: "File" label using all remaining space

## Impact
- ✅ "File" column now uses full available width on mobile devices
- ✅ Improved readability and layout consistency
- ✅ Better user experience on mobile and tablet viewports
- ✅ No changes to desktop layout (> 768px)

## Testing
Created a test HTML page demonstrating the before/after behavior. Screenshots confirm the fix works correctly at:
- Mobile viewport (375px width)
- Tablet viewport (768px width)

## Files Modified
- `templates/index.html` - Updated mobile responsive CSS (2 lines changed)
