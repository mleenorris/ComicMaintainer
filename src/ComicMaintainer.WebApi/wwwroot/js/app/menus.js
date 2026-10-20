        // ComicMaintainer SPA — dropdown and action menus.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Tracks a dropdown menu that has been temporarily relocated to
        // <body> while open. The menu uses position: fixed, but several of its
        // natural ancestors (e.g. the series issue cards) establish a
        // containing block for fixed descendants via CSS `transform` (the hover
        // lift) and clip overflow, which re-bases and cuts off the menu. Moving
        // the open menu to <body> guarantees its fixed coordinates resolve
        // against the viewport so it is positioned and shown correctly.
        let portaledDropdown = null; // { menu, placeholder }

        function restorePortaledDropdown() {
            if (!portaledDropdown) {
                return;
            }
            const { menu, placeholder } = portaledDropdown;
            if (placeholder && placeholder.parentNode) {
                placeholder.parentNode.insertBefore(menu, placeholder);
                placeholder.remove();
            }
            // Clear inline positioning so the menu doesn't retain stale
            // coordinates the next time it is opened.
            menu.style.top = '';
            menu.style.left = '';
            portaledDropdown = null;
        }

        function portalDropdownToBody(dropdown) {
            // Only portal once; if it's already relocated, leave it in place.
            if (portaledDropdown && portaledDropdown.menu === dropdown) {
                return;
            }
            restorePortaledDropdown();
            const placeholder = document.createComment('dropdown-placeholder');
            dropdown.parentNode.insertBefore(placeholder, dropdown);
            document.body.appendChild(dropdown);
            portaledDropdown = { menu: dropdown, placeholder };
        }

        function toggleDropdown(event, filepath) {
            event.stopPropagation();
            
            const dropdownId = getDropdownId(filepath);
            const dropdown = document.getElementById(dropdownId);
            
            if (!dropdown) {
                console.error('Dropdown not found for filepath:', filepath, 'ID:', dropdownId);
                return;
            }
            
            // Remember whether this menu was already open before we close
            // everything (closing also restores any portaled menu).
            const isCurrentlyShown = dropdown.classList.contains('show');
            closeAllDropdowns();
            if (isCurrentlyShown) {
                // It was open; closeAllDropdowns has now hidden it. Toggle off.
                return;
            }

            // Show this dropdown
            dropdown.classList.add('show');

            // Position it relative to the button
            {
                // Remove any previous positioning class
                dropdown.classList.remove('show-above');
                // Relocate to <body> so position: fixed resolves against the
                // viewport regardless of any transformed/clipping ancestor.
                portalDropdownToBody(dropdown);

                // Get the dropdown button position
                const button = event.target.closest('.dropdown-toggle');
                if (button) {
                    const buttonRect = button.getBoundingClientRect();
                    
                    // Temporarily show dropdown to get its dimensions
                    dropdown.style.visibility = 'hidden';
                    dropdown.style.display = 'block';
                    const dropdownRect = dropdown.getBoundingClientRect();
                    dropdown.style.visibility = '';
                    dropdown.style.display = '';
                    
                    const viewportHeight = window.innerHeight;
                    const viewportWidth = window.innerWidth;
                    
                    // Constants for positioning calculations
                    const MARGIN = 20; // Safety margin to prevent edge cutoff
                    const DROPDOWN_OFFSET = 2; // Spacing between button and dropdown
                    const VIEWPORT_MARGIN = 10; // Minimum margin from viewport edges
                    
                    // Check if dropdown would overflow the bottom of the viewport
                    const spaceBelow = viewportHeight - buttonRect.bottom;
                    const spaceAbove = buttonRect.top;
                    
                    // Determine if we should show above or below
                    const notEnoughSpaceBelow = spaceBelow < (dropdownRect.height + MARGIN);
                    const inBottomHalf = buttonRect.bottom > (viewportHeight / 2);
                    const moreSpaceAbove = spaceAbove > spaceBelow;
                    const showAbove = notEnoughSpaceBelow || (inBottomHalf && moreSpaceAbove);
                    
                    // Calculate position
                    let top, left;
                    
                    if (showAbove) {
                        // Position above the button
                        top = buttonRect.top - dropdownRect.height - DROPDOWN_OFFSET;
                        dropdown.classList.add('show-above');
                    } else {
                        // Position below the button
                        top = buttonRect.bottom + DROPDOWN_OFFSET;
                    }
                    
                    // Align to the right edge of the button
                    left = buttonRect.right - dropdownRect.width;
                    
                    // Make sure dropdown doesn't go off the left edge of the viewport
                    if (left < VIEWPORT_MARGIN) {
                        left = VIEWPORT_MARGIN;
                    }
                    
                    // Make sure dropdown doesn't go off the right edge of the viewport
                    if (left + dropdownRect.width > viewportWidth - VIEWPORT_MARGIN) {
                        left = viewportWidth - dropdownRect.width - VIEWPORT_MARGIN;
                    }
                    
                    // Apply the position
                    dropdown.style.top = `${top}px`;
                    dropdown.style.left = `${left}px`;
                }
            }
        }
        
        function closeAllDropdowns() {
            document.querySelectorAll('.dropdown-menu.show').forEach(menu => {
                menu.classList.remove('show');
                menu.classList.remove('show-above');
            });
            // Return any portaled menu to its original location in the DOM.
            restorePortaledDropdown();
            // Also close filter dropdown
            const filterMenu = document.getElementById('filterDropdownMenu');
            if (filterMenu) {
                filterMenu.classList.remove('show');
            }
        }
        
        // Close dropdowns when clicking outside
        document.addEventListener('click', function(event) {
            // The open action menu may be portaled to <body> (outside its
            // originating .file-actions-dropdown), so also treat clicks within
            // a .dropdown-menu as "inside".
            if (!event.target.closest('.file-actions-dropdown') && !event.target.closest('.dropdown-menu')) {
                closeAllDropdowns();
            }
            // Close header filter dropdown when clicking outside
            if (!event.target.closest('.header-filter-dropdown')) {
                const filterMenu = document.getElementById('headerFilterMenu');
                if (filterMenu) {
                    filterMenu.classList.remove('show');
                }
            }
            // Close header sort dropdown when clicking outside
            if (!event.target.closest('.header-sort-dropdown')) {
                const sortMenu = document.getElementById('headerSortMenu');
                if (sortMenu) {
                    sortMenu.classList.remove('show');
                }
            }
            // Close settings dropdown when clicking outside
            if (!event.target.closest('.settings-menu-wrapper')) {
                const settingsMenu = document.getElementById('settingsDropdownMenu');
                if (settingsMenu) {
                    settingsMenu.classList.remove('show');
                }
            }
            // Close action dropdowns when clicking outside
            if (!event.target.closest('.action-dropdown')) {
                closeAllActionDropdowns();
            }
        });
        
        // Function to toggle action dropdowns
        function toggleActionDropdown(event, dropdownId) {
            event.stopPropagation();
            
            const dropdown = document.getElementById(dropdownId);
            if (!dropdown) {
                console.error('Action dropdown not found:', dropdownId);
                return;
            }
            
            // Close all other action dropdowns
            document.querySelectorAll('.action-dropdown-menu.show').forEach(menu => {
                if (menu.id !== dropdownId) {
                    menu.classList.remove('show');
                }
            });
            
            // Toggle this dropdown
            dropdown.classList.toggle('show');
        }
        
        // Function to close all action dropdowns
        function closeAllActionDropdowns() {
            document.querySelectorAll('.action-dropdown-menu.show').forEach(menu => {
                menu.classList.remove('show');
            });
        }
