        // ComicMaintainer SPA — modal accessibility (focus trap, Escape) and library keyboard shortcuts.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // ---------------------------------------------------------------
        // Shared modal accessibility helper
        // ---------------------------------------------------------------
        // The library page has 15 modals that are opened/closed from many
        // different call sites, using a mix of `classList.add('active')`,
        // `classList.add('show')` and inline `style.display`. Rather than
        // patching every call site, this helper observes the modals' class and
        // style attributes and reacts to visibility changes centrally. That way
        // any future modal or open/close path automatically gets:
        //   - aria-hidden kept in sync with actual visibility
        //   - initial focus moved into the dialog
        //   - Tab/Shift+Tab trapped inside the dialog
        //   - Escape to close (delegates to the modal's own close button so the
        //     existing close handlers still run their cleanup)
        //   - focus restored to whatever was focused before the modal opened
        const modalA11y = {
            // Stack of { modal, previousFocus } so nested modals unwind correctly.
            stack: [],
            observer: null
        };

        const FOCUSABLE_SELECTOR = [
            'a[href]',
            'button:not([disabled])',
            'input:not([disabled]):not([type="hidden"])',
            'select:not([disabled])',
            'textarea:not([disabled])',
            '[tabindex]:not([tabindex="-1"])'
        ].join(', ');

        function isModalVisible(modal) {
            // `.modal` is display:none by default; `.active`/`.show` reveal it.
            // Some call sites also set an inline display, so fall back to the
            // computed style rather than trusting the class alone.
            if (!modal || !modal.isConnected) return false;
            if (modal.classList.contains('active') || modal.classList.contains('show')) return true;
            return window.getComputedStyle(modal).display !== 'none';
        }

        function getFocusableElements(container) {
            return Array.from(container.querySelectorAll(FOCUSABLE_SELECTOR))
                .filter(el => el.offsetParent !== null || el === document.activeElement);
        }

        function focusFirstElementInModal(modal) {
            const focusable = getFocusableElements(modal);
            // Prefer the first non-close control so keyboard users land on the
            // dialog's primary content rather than the "×" button.
            const preferred = focusable.find(el => !el.classList.contains('close-btn')) || focusable[0];
            if (preferred) {
                preferred.focus();
            } else {
                if (!modal.hasAttribute('tabindex')) modal.setAttribute('tabindex', '-1');
                modal.focus();
            }
        }

        function handleModalOpened(modal) {
            if (modalA11y.stack.some(entry => entry.modal === modal)) return;
            const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
            modalA11y.stack.push({ modal, previousFocus });
            modal.setAttribute('aria-hidden', 'false');
            // Defer so content rendered during the same tick is focusable.
            window.setTimeout(() => {
                if (!isModalVisible(modal)) return;
                // Respect any explicit focus the opening code already applied.
                if (modal.contains(document.activeElement)) return;
                focusFirstElementInModal(modal);
            }, 0);
        }

        function handleModalClosed(modal) {
            modal.setAttribute('aria-hidden', 'true');
            const index = modalA11y.stack.findIndex(entry => entry.modal === modal);
            if (index === -1) return;
            const [entry] = modalA11y.stack.splice(index, 1);
            // Only restore focus if focus is still inside (or was lost by) the
            // modal that just closed, so we don't steal focus from the user.
            const activeEl = document.activeElement;
            const focusLost = !activeEl || activeEl === document.body || modal.contains(activeEl);
            if (focusLost && entry.previousFocus && entry.previousFocus.isConnected) {
                entry.previousFocus.focus();
            }
        }

        function getTopmostVisibleModal() {
            for (let i = modalA11y.stack.length - 1; i >= 0; i--) {
                if (isModalVisible(modalA11y.stack[i].modal)) return modalA11y.stack[i].modal;
            }
            return null;
        }

        function closeModalViaCloseButton(modal) {
            // Reuse the modal's own close control so its bespoke cleanup runs.
            const closeBtn = modal.querySelector('.modal-header .close-btn')
                || modal.querySelector('.close-btn');
            if (closeBtn) {
                closeBtn.click();
                return true;
            }
            return false;
        }

        function handleModalKeydown(event) {
            const modal = getTopmostVisibleModal();
            if (!modal) return;

            if (event.key === 'Escape') {
                if (closeModalViaCloseButton(modal)) event.preventDefault();
                return;
            }

            if (event.key !== 'Tab') return;

            const focusable = getFocusableElements(modal);
            if (focusable.length === 0) {
                event.preventDefault();
                return;
            }
            const first = focusable[0];
            const last = focusable[focusable.length - 1];
            const focusOutside = !modal.contains(document.activeElement);
            if (event.shiftKey && (focusOutside || document.activeElement === first)) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && (focusOutside || document.activeElement === last)) {
                event.preventDefault();
                first.focus();
            }
        }

        function initModalAccessibility() {
            const modals = Array.from(document.querySelectorAll('.modal'));
            if (modals.length === 0) return;

            modals.forEach(modal => {
                if (!modal.hasAttribute('role')) modal.setAttribute('role', 'dialog');
                if (!modal.hasAttribute('aria-modal')) modal.setAttribute('aria-modal', 'true');
                modal.setAttribute('aria-hidden', isModalVisible(modal) ? 'false' : 'true');
                // The "×" glyph is meaningless to screen readers.
                modal.querySelectorAll('.close-btn').forEach(btn => {
                    if (!btn.hasAttribute('aria-label')) btn.setAttribute('aria-label', 'Close');
                });
                if (isModalVisible(modal)) handleModalOpened(modal);
            });

            modalA11y.observer = new MutationObserver(mutations => {
                const seen = new Set();
                mutations.forEach(mutation => {
                    const modal = mutation.target;
                    if (seen.has(modal)) return;
                    seen.add(modal);
                    const visible = isModalVisible(modal);
                    const tracked = modalA11y.stack.some(entry => entry.modal === modal);
                    if (visible && !tracked) {
                        handleModalOpened(modal);
                    } else if (!visible && tracked) {
                        handleModalClosed(modal);
                    }
                });
            });

            modals.forEach(modal => {
                modalA11y.observer.observe(modal, { attributes: true, attributeFilter: ['class', 'style'] });
            });

            // Capture phase so the reader/library shortcut handlers don't
            // swallow Escape before the topmost dialog gets a chance to close.
            document.addEventListener('keydown', handleModalKeydown, true);
        }

        // =================================================================
        // Library keyboard shortcuts
        // =================================================================
        //
        // The reader has had shortcuts and a "?" overlay for a long time; the
        // library page had neither, so every action needed the mouse and there
        // was nothing to discover. The bindings below mirror the conventions
        // users already expect from other library apps ("/" to search, "g"
        // then a letter to navigate, "?" for help).
        //
        // Two rules keep them out of the way: they never fire while the user
        // is typing, and they never fire while a dialog is open (the modal
        // handler owns the keyboard then, including Escape).

        const KEYBOARD_SEQUENCE_TIMEOUT_MS = 1200;
        let pendingShortcutPrefix = null;
        let pendingShortcutPrefixTimer = null;

        function openKeyboardShortcutsModal() {
            const modal = document.getElementById('keyboardShortcutsModal');
            if (modal) modal.classList.add('active');
        }

        function closeKeyboardShortcutsModal() {
            const modal = document.getElementById('keyboardShortcutsModal');
            if (modal) modal.classList.remove('active');
        }

        /**
         * True when the keystroke belongs to something the user is editing, in
         * which case a bare letter must be typed rather than interpreted.
         */
        function isTypingTarget(target) {
            if (!(target instanceof HTMLElement)) return false;
            if (target.isContentEditable) return true;
            const tag = target.tagName;
            return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
        }

        /**
         * The header search/filter controls are hidden on the Overview route
         * (see applyRouteVisibility), so their shortcuts must stay inert there
         * rather than swallowing the keystroke and focusing nothing.
         */
        function isShortcutControlAvailable(element) {
            return !!element && element.offsetParent !== null;
        }

        function clearShortcutPrefix() {
            pendingShortcutPrefix = null;
            if (pendingShortcutPrefixTimer) {
                clearTimeout(pendingShortcutPrefixTimer);
                pendingShortcutPrefixTimer = null;
            }
        }

        function startShortcutPrefix(key) {
            clearShortcutPrefix();
            pendingShortcutPrefix = key;
            // A half-finished sequence must not silently capture a keystroke
            // made a minute later.
            pendingShortcutPrefixTimer = setTimeout(clearShortcutPrefix, KEYBOARD_SEQUENCE_TIMEOUT_MS);
        }

        function handleLibraryShortcut(event) {
            // Never intercept a browser/OS chord (Ctrl+F, Cmd+R, ...).
            if (event.ctrlKey || event.metaKey || event.altKey) return;

            const searchInput = document.getElementById('headerSearchInput');

            // Escape inside the search box clears it. Handled before the
            // typing guard because this is the one shortcut that is *about*
            // the field the user is typing in.
            if (event.key === 'Escape' && document.activeElement === searchInput) {
                if (clearHeaderSearch()) event.preventDefault();
                return;
            }

            if (isTypingTarget(event.target)) return;

            // A visible dialog owns the keyboard (see handleModalKeydown).
            if (typeof getTopmostVisibleModal === 'function' && getTopmostVisibleModal()) return;

            if (pendingShortcutPrefix === 'g') {
                const key = event.key.toLowerCase();
                clearShortcutPrefix();
                if (key === 'h') {
                    event.preventDefault();
                    navigate('#/home');
                } else if (key === 'l') {
                    event.preventDefault();
                    navigate('#/library');
                } else if (key === 's') {
                    event.preventDefault();
                    navigate('#/settings');
                }
                return;
            }

            switch (event.key) {
                case '/':
                    if (isShortcutControlAvailable(searchInput)) {
                        event.preventDefault();
                        searchInput.focus();
                        searchInput.select();
                    }
                    break;
                case '?':
                    event.preventDefault();
                    openKeyboardShortcutsModal();
                    break;
                case 'g':
                case 'G':
                    startShortcutPrefix('g');
                    break;
                case 'r':
                case 'R':
                    if (isShortcutControlAvailable(searchInput)) {
                        event.preventDefault();
                        refreshFiles();
                    }
                    break;
                case 'f':
                case 'F': {
                    const toggle = document.getElementById('headerFilterToggle');
                    if (isShortcutControlAvailable(toggle)) {
                        event.preventDefault();
                        toggle.click();
                    }
                    break;
                }
                default:
                    break;
            }
        }

        function initLibraryKeyboardShortcuts() {
            // Only the library page has these controls; main.js is not loaded
            // anywhere else today, but the guard keeps that assumption cheap.
            if (!document.getElementById('headerSearchInput')) return;
            syncHeaderSearchClear();
            document.addEventListener('keydown', handleLibraryShortcut);
        }
