        // ComicMaintainer SPA — the file and series selection, and the small
        // formatting helpers the library's views share.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        function formatFileSize(bytes) {
            if (bytes < 1024) return bytes + ' B';
            if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
            if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
            if (bytes < 1024 * 1024 * 1024 * 1024) return (bytes / (1024 * 1024 * 1024)).toFixed(1) + ' GB';
            return (bytes / (1024 * 1024 * 1024 * 1024)).toFixed(1) + ' TB';
        }
        
        function formatModifiedDate(timestamp) {
            const date = new Date(timestamp * 1000); // Convert Unix timestamp to milliseconds
            const now = new Date();
            const diffMs = now - date;
            const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
            
            // Show relative time for recent files. A time in the future is
            // never "N days ago": up to a minute ahead (the server's clock a
            // little ahead of the browser's) is "Just now", and further ahead
            // it is shown as a date.
            if (diffMs < 0) {
                if (diffMs >= -60 * 1000) return 'Just now';
            } else if (diffDays === 0) {
                const diffHours = Math.floor(diffMs / (1000 * 60 * 60));
                if (diffHours === 0) {
                    const diffMinutes = Math.floor(diffMs / (1000 * 60));
                    if (diffMinutes < 1) return 'Just now';
                    return `${diffMinutes} min ago`;
                }
                return `${diffHours}h ago`;
            } else if (diffDays === 1) {
                return 'Yesterday';
            } else if (diffDays < 7) {
                return `${diffDays} days ago`;
            }
            
            // Show date for older (or future) files
            const year = date.getFullYear();
            const month = String(date.getMonth() + 1).padStart(2, '0');
            const day = String(date.getDate()).padStart(2, '0');
            
            // Show year only if different from current year
            if (year !== now.getFullYear()) {
                return `${year}-${month}-${day}`;
            }
            return `${month}-${day}`;
        }

        // Extract just the filename portion from a relative or absolute path.
        function extractDisplayName(filepath) {
            if (!filepath) return '';
            const parts = filepath.split(/[/\\]/);
            return parts[parts.length - 1] || filepath;
        }

        /**
         * The id of a file's Actions menu. The renderers and toggleDropdown()
         * both derive it from the file path, so it must be injective: two
         * files may never share an id, or getElementById() would open (and the
         * menu items would act on) the first file's menu when the second
         * file's button is clicked. The path's base64url form is unique per
         * path and uses only id- and selector-safe characters
         * ([A-Za-z0-9_-]); squashing every other character to '_' was not
         * unique ('第1話' and '第1巻', 'Saga 001' and 'Saga_001').
         */
        function getDropdownId(filepath) {
            return 'dropdown-' + encodeFilePathForUrl(String(filepath));
        }

        // The issues of the open series page, which is the only place a
        // whole list of files can be selected at once.
        function getCurrentSelectableFilePaths() {
            const cached = currentSeriesDetailId ? seriesIssuesCache.get(currentSeriesDetailId) : null;
            if (!cached || !Array.isArray(cached.issues)) {
                return [];
            }

            return cached.issues
                .map(issue => issue.file_path)
                .filter(path => typeof path === 'string' && path.length > 0);
        }

        function updateSeriesIssueSelectionState(filepath, checked) {
            document.querySelectorAll('.series-issue-card[data-file-path]').forEach(card => {
                if (card.dataset.filePath === filepath) {
                    card.classList.toggle('series-issue-card--selected', checked);
                    // The clicked checkbox is already in the right state, but
                    // the rest of a shift-click range is only selected in the
                    // model, so push the state onto those boxes here.
                    const checkbox = card.querySelector('.series-issue-select input[type="checkbox"]');
                    if (checkbox && checkbox.checked !== checked) {
                        checkbox.checked = checked;
                    }
                }
            });
        }

        // Records whether the interaction that is about to toggle an issue
        // checkbox was made with Shift held. The modifier is only available on
        // the originating mouse/key event: the change event that follows
        // carries no modifier state, and the click a <label> forwards to its
        // checkbox is synthesised and may drop it. A mousedown/keydown on the
        // label happens exactly once per interaction, whichever part of the
        // control was hit, so it is the reliable place to read it.
        function rememberSeriesIssueRangeModifier(event) {
            seriesIssueRangeSelectArmed = !!(event && event.shiftKey);
        }

        // Checkbox change handler for the issue cards in the series detail
        // view. With Shift held, the issue's new state is applied to every
        // issue between the previously clicked one and this one; otherwise this
        // is a plain toggle. Either way the issue becomes the anchor for the
        // next shift-click.
        function toggleSeriesIssueSelection(filepath, checked) {
            const range = seriesIssueRangeSelectArmed
                ? getSeriesIssueSelectionRange(lastSelectedIssuePath, filepath)
                : null;
            seriesIssueRangeSelectArmed = false;
            lastSelectedIssuePath = filepath;
            // Without a usable anchor a shift-click behaves like a plain click.
            if (!range) {
                toggleFileSelection(filepath, checked);
                return;
            }
            applySeriesIssueRangeSelection(range, checked);
        }

        // Collects the file paths of the issue cards between two issues,
        // inclusive, in the order they are displayed. The grid orders cards by
        // issue number and interleaves missing-issue placeholders (which carry
        // no data-file-path), so the rendered DOM — not the cached issue array
        // — defines what "in between" means. Returns null when the range
        // cannot be resolved, e.g. the anchor is no longer rendered.
        function getSeriesIssueSelectionRange(anchorPath, targetPath) {
            if (!anchorPath || !targetPath || anchorPath === targetPath) return null;
            const grid = document.querySelector('#seriesDetailPanel .series-issues-grid');
            if (!grid) return null;
            const paths = Array.from(grid.querySelectorAll('.series-issue-card[data-file-path]'))
                .map(card => card.dataset.filePath)
                .filter(path => typeof path === 'string' && path.length > 0);
            const anchorIndex = paths.indexOf(anchorPath);
            const targetIndex = paths.indexOf(targetPath);
            if (anchorIndex === -1 || targetIndex === -1) return null;
            return paths.slice(
                Math.min(anchorIndex, targetIndex),
                Math.max(anchorIndex, targetIndex) + 1);
        }

        // Applies one selection state to a whole range of issues, then refreshes
        // the shared selection UI once rather than per issue.
        function applySeriesIssueRangeSelection(paths, checked) {
            paths.forEach(path => {
                if (checked) {
                    selectedFiles.add(path);
                } else {
                    selectedFiles.delete(path);
                }
                updateSeriesIssueSelectionState(path, checked);
            });
            // Shift-clicking also extends the document's text selection, which
            // would leave the issues in the range highlighted.
            const textSelection = typeof window.getSelection === 'function' ? window.getSelection() : null;
            if (textSelection && typeof textSelection.removeAllRanges === 'function') {
                textSelection.removeAllRanges();
            }
            updateSelectInfo();
            updateSelectAllCheckbox();
        }

        function toggleSelectAll(checked) {
            getCurrentSelectableFilePaths().forEach(filepath => {
                if (checked) {
                    selectedFiles.add(filepath);
                } else {
                    selectedFiles.delete(filepath);
                }
            });
            // Only the series page offers this checkbox.
            if (currentSeriesDetailId) {
                renderSeriesDetail(currentSeriesDetailId);
            }
        }

        function toggleFileSelection(filepath, checked) {
            if (checked) {
                selectedFiles.add(filepath);
            } else {
                selectedFiles.delete(filepath);
            }
            updateSeriesIssueSelectionState(filepath, checked);
            updateSelectInfo();
            updateSelectAllCheckbox();
        }
        
        function updateSelectInfo() {
            const count = selectedFiles.size;
            const info = document.getElementById('selectInfo');
            const batchBtn = document.getElementById('batchUpdateBtn');
            const deleteSelectedBtn = document.getElementById('deleteSelectedBtn');
            const processSelectedItem = document.getElementById('processSelectedItem');
            const renameSelectedItem = document.getElementById('renameSelectedItem');
            const normalizeSelectedItem = document.getElementById('normalizeSelectedItem');
            const markSelectedReadItem = document.getElementById('markSelectedReadItem');
            const markSelectedUnreadItem = document.getElementById('markSelectedUnreadItem');
            const clearSelectedStatusItem = document.getElementById('clearSelectedStatusItem');
            const removeMetadataSelectedItem = document.getElementById('removeMetadataSelectedItem');
            const emailSelectedItem = document.getElementById('emailSelectedItem');
            const updateSeriesCoversSelectedItem = document.getElementById('updateSeriesCoversSelectedItem');
            const canUpdateSelectedSeriesCovers = getSelectedSeriesTitlesForCoverUpdate().length > 0;
            
            if (count === 0) {
                info.textContent = 'No files selected';
                batchBtn.disabled = true;
                if (deleteSelectedBtn) deleteSelectedBtn.disabled = true;
                if (processSelectedItem) processSelectedItem.disabled = true;
                if (renameSelectedItem) renameSelectedItem.disabled = true;
                if (normalizeSelectedItem) normalizeSelectedItem.disabled = true;
                if (markSelectedReadItem) markSelectedReadItem.disabled = true;
                if (markSelectedUnreadItem) markSelectedUnreadItem.disabled = true;
                if (clearSelectedStatusItem) clearSelectedStatusItem.disabled = true;
                if (removeMetadataSelectedItem) removeMetadataSelectedItem.disabled = true;
                if (emailSelectedItem) emailSelectedItem.disabled = true;
            } else {
                info.textContent = describeCurrentSelection(count);
                batchBtn.disabled = false;
                if (deleteSelectedBtn) deleteSelectedBtn.disabled = false;
                if (processSelectedItem) processSelectedItem.disabled = false;
                if (renameSelectedItem) renameSelectedItem.disabled = false;
                if (normalizeSelectedItem) normalizeSelectedItem.disabled = false;
                if (markSelectedReadItem) markSelectedReadItem.disabled = false;
                if (markSelectedUnreadItem) markSelectedUnreadItem.disabled = false;
                if (clearSelectedStatusItem) clearSelectedStatusItem.disabled = false;
                if (removeMetadataSelectedItem) removeMetadataSelectedItem.disabled = false;
                if (emailSelectedItem) emailSelectedItem.disabled = false;
            }
            if (updateSeriesCoversSelectedItem) updateSeriesCoversSelectedItem.disabled = !canUpdateSelectedSeriesCovers;
            syncSeriesSelectionControls();
        }

        // Build the "N file(s) selected" message, also noting how many series
        // are selected when operating from the series list view.
        function describeCurrentSelection(fileCount) {
            const fileText = `${fileCount} file${fileCount > 1 ? 's' : ''} selected`;
            if (!currentSeriesDetailId && selectedSeries.size > 0) {
                const seriesCount = selectedSeries.size;
                return `${seriesCount} series · ${fileText}`;
            }
            return fileText;
        }

        // Keep the series-list selection toolbar (count text + select-all
        // checkbox indeterminate state) in sync with the current selection.
        function syncSeriesSelectionControls() {
            const countEl = document.getElementById('seriesSelectionCount');
            if (countEl) {
                const n = selectedSeries.size;
                countEl.textContent = n > 0 ? `${n} series selected` : 'No series selected';
            }
            const selectAll = document.getElementById('seriesSelectAll');
            if (selectAll) {
                const total = seriesLibrary.length;
                const selected = seriesLibrary.reduce(
                    (acc, s) => acc + (selectedSeries.has(s.id) ? 1 : 0), 0);
                selectAll.checked = total > 0 && selected === total;
                selectAll.indeterminate = selected > 0 && selected < total;
            }
            const clearBtn = document.getElementById('seriesSelectionClearBtn');
            if (clearBtn) {
                clearBtn.hidden = selectedSeries.size === 0;
            }
            const emailBtn = document.getElementById('seriesSelectionEmailBtn');
            if (emailBtn) {
                emailBtn.hidden = selectedSeries.size === 0;
                emailBtn.disabled = Array.from(selectedSeries).some(id => selectedSeriesPathLoads.has(id));
            }
        }

        function updateSelectAllCheckbox() {
            const selectAllCheckbox = document.getElementById('selectAll');
            if (!selectAllCheckbox) return;

            const visibleFilePaths = getCurrentSelectableFilePaths();
            if (visibleFilePaths.length === 0) {
                selectAllCheckbox.checked = false;
                selectAllCheckbox.indeterminate = false;
            } else {
                const allSelected = visibleFilePaths.every(filepath => selectedFiles.has(filepath));
                const someSelected = visibleFilePaths.some(filepath => selectedFiles.has(filepath));

                selectAllCheckbox.checked = allSelected;
                selectAllCheckbox.indeterminate = someSelected && !allSelected;
            }
        }

        // Empty the whole selection (files and series) and show it at once:
        // untick the rendered selection boxes, drop the selected highlights
        // and reset the selection bar, so its batch actions are disabled. For
        // code that clears the selection outside a re-render, which may come
        // late or never (a delete job cancelled before it removes a file, or
        // the event stream being down). Updates in place, keeping the scroll
        // position.
        function clearSelection() {
            selectedFiles.clear();
            resetSeriesSelectionState();
            document.querySelectorAll('#fileList input[type="checkbox"]').forEach(checkbox => {
                checkbox.checked = false;
            });
            document.querySelectorAll('#fileList .series-issue-card--selected').forEach(card => {
                card.classList.remove('series-issue-card--selected');
            });
            updateSeriesSelectionUI();
            updateSelectInfo();
            updateSelectAllCheckbox();
        }
