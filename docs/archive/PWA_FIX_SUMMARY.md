# PWA Installation Fix for Android

## Problem
Users on Android mobile devices were not seeing the install option for the web app.

## Root Cause
The `beforeinstallprompt` event handler in `templates/index.html` was calling `e.preventDefault()`, which prevented the browser's native installation prompt from appearing on Android devices. While this was intended to create a custom install button, it blocked the default installation flow that Android users typically rely on.

## Solution
Removed the `e.preventDefault()` call from the `beforeinstallprompt` event listener. This allows:

1. **Native Android prompt**: Chrome on Android will now show its automatic installation banner/prompt
2. **Browser menu option**: Users can still access "Install app" from Chrome's three-dot menu
3. **Custom install button**: The app's custom "📱 Install App" button in the settings menu still works

## Changes Made

### File: `templates/index.html`
**Before:**
```javascript
window.addEventListener('beforeinstallprompt', (e) => {
    console.log('PWA: beforeinstallprompt event fired');
    // Prevent the mini-infobar from appearing on mobile
    e.preventDefault();  // ← This blocked native Android prompts
    deferredPrompt = e;
    // ...
});
```

**After:**
```javascript
window.addEventListener('beforeinstallprompt', (e) => {
    console.log('PWA: beforeinstallprompt event fired');
    // Don't prevent the default behavior - allow native Android install prompt
    // Just stash the event so we can also provide a custom install button
    deferredPrompt = e;
    // ...
});
```

### File: `README.md`
Updated the Android installation instructions to mention:
- The automatic install prompt that now appears
- Alternative installation methods (browser menu, custom button)

## Benefits
- **Better UX**: Android users see the familiar native installation prompt
- **Multiple options**: Users can choose between:
  1. The automatic browser prompt (most discoverable)
  2. Browser menu → "Install app"
  3. App settings menu → "📱 Install App"
- **Standards compliant**: Follows PWA best practices for installation discoverability

## Technical Details
- PWA manifest remains unchanged and valid
- Service worker registration unchanged
- Custom install button functionality preserved
- No breaking changes to existing functionality

## Testing Checklist
- [x] Validate manifest.json structure
- [x] Verify JavaScript syntax
- [x] Check service worker is valid
- [x] Confirm e.preventDefault() removed
- [x] Verify deferredPrompt still captured
- [x] Update documentation

## References
- [Web.dev: How to provide your own in-app install experience](https://web.dev/customize-install/)
- [MDN: beforeinstallprompt event](https://developer.mozilla.org/en-US/docs/Web/API/Window/beforeinstallprompt_event)
