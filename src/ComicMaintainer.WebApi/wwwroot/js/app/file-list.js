        // ComicMaintainer SPA — file list rendering, folders and file/issue selection.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        function renderFileRow(file, dir) {
            const isSelected = selectedFiles.has(file.relative_path);
            const fileSize = formatFileSize(file.size);
            const modifiedDate = formatModifiedDate(file.modified);

            let readIcon = '';
            let readTitle = '';
            if (file.read) {
                readIcon = '👁️';
                readTitle = 'Read';
            }

            let statusIcon = '';
            let statusTitle = '';
            let statusClass = '';

            if (file.duplicate) {
                statusIcon = '🔁';
                statusTitle = 'Duplicate';
                statusClass = 'status-duplicate';
            } else if (file.renamed && file.normalized) {
                statusIcon = '✅';
                statusTitle = 'Processed (Renamed & Normalized)';
                statusClass = 'status-marked';
            } else if (file.renamed && !file.normalized) {
                statusIcon = '🔵';
                statusTitle = 'Renamed';
                statusClass = 'status-renamed';
            } else if (file.normalized && !file.renamed) {
                statusIcon = '🔴';
                statusTitle = 'Normalized';
                statusClass = 'status-normalized';
            } else {
                statusIcon = '⚠️';
                statusTitle = 'Unmarked';
                statusClass = 'status-unmarked';
            }

            const filenameParts = truncateFilenameMiddle(file.name);
            const filenameHtml = filenameParts.end
                ? `<span class="file-name-start">${escapeHtml(filenameParts.start)}</span><span class="file-name-end">${escapeHtml(filenameParts.end)}</span>`
                : `<span class="file-name-content">${escapeHtml(filenameParts.start)}</span>`;

            return `
                <div class="file-item ${statusClass}">
                    <input type="checkbox"
                           ${isSelected ? 'checked' : ''}
                           onchange="toggleFileSelection('${escapeJs(file.relative_path)}', this.checked)">
                    <div class="status-badge" title="${statusTitle}">
                        <span aria-hidden="true">${statusIcon}</span>
                        <span class="visually-hidden">${escapeHtml(statusTitle)}</span>
                    </div>
                    <div>
                        <div class="file-name" title="${escapeHtml(file.name)}">
                            ${readIcon ? `<span class="read-indicator" title="${readTitle}"><span aria-hidden="true">${readIcon}</span><span class="visually-hidden">${escapeHtml(readTitle)}</span></span> ` : ''}${filenameHtml}
                        </div>
                        ${!dir ? `<div class="file-path">${escapeHtml(file.relative_path)}</div>` : ''}
                    </div>
                    <div>${fileSize}</div>
                    <div style="color: var(--text-muted); font-size: 13px;">${modifiedDate}</div>
                    <div class="file-actions">
                        <div class="file-actions-dropdown">
                            <button class="dropdown-toggle" onclick="toggleDropdown(event, '${escapeJs(file.relative_path)}')">
                                Actions
                            </button>
                            <div class="dropdown-menu" id="${getDropdownId(file.relative_path)}">
                                <button class="dropdown-item" onclick="showFileInfo('${escapeJs(file.relative_path)}', '${escapeJs(file.name)}'); closeAllDropdowns();">
                                    ${uiIcon('info')} Info
                                </button>
                                <button class="dropdown-item" onclick="viewTags('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    ${uiIcon('eye')} View/Edit
                                </button>
                                <button class="dropdown-item" onclick="readComic('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    ${uiIcon('book-open')} Read Comic
                                </button>
                                <button class="dropdown-item" data-requires-write onclick="openEmailSendModalForFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    ${uiIcon('mail')} Email to Ereader…
                                </button>
                                ${file.duplicate
                                    ? `<button class="dropdown-item" onclick="openDuplicateReviewModal('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                        ${uiIcon('copy')} Review Duplicate
                                    </button>`
                                    : ''
                                }
                                <div class="dropdown-divider"></div>
                                ${file.read
                                    ? `<button class="dropdown-item" onclick="markFileUnread('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                        ${uiIcon('library')} Mark Unread
                                    </button>`
                                    : `<button class="dropdown-item" onclick="markFileRead('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                        ${uiIcon('check-circle')} Mark Read
                                    </button>`
                                }
                                <div class="dropdown-divider"></div>
                                <button class="dropdown-item" onclick="processSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    ${uiIcon('play')} Process
                                </button>
                                <button class="dropdown-item" onclick="renameSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    ${uiIcon('pencil')} Rename
                                </button>
                                <button class="dropdown-item" onclick="normalizeSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    ${uiIcon('tag')} Normalize
                                </button>
                                <button class="dropdown-item" onclick="removeFileMetadata('${escapeJs(file.relative_path)}'); closeAllDropdowns();" title="Open the archive, delete the embedded ComicInfo.xml, and mark the file as unprocessed so it can be re-processed.">
                                    ${uiIcon('eraser')} Remove Metadata
                                </button>
                                <div class="dropdown-divider"></div>
                                <button class="dropdown-item" onclick="deleteSingleFile('${escapeJs(file.relative_path)}'); closeAllDropdowns();">
                                    ${uiIcon('trash')} Delete
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            `;
        }

        // ---------------------------------------------------------------
        // Incremental list rendering helpers (see renderFileList()).
        //
        // Each cache maps a key to `{ html, el }`, where `html` is the markup
        // the node was built from. A node is only rebuilt when its markup
        // changed or when it is no longer attached to the expected parent
        // (which happens whenever some other code path replaces the whole list,
        // e.g. to show a loading or empty state) — that check makes the caches
        // self-healing rather than something every teardown site must reset.
        // ---------------------------------------------------------------
        const folderSectionCache = new Map();
        const fileListHeaderCache = new Map();

        function reuseOrBuildListNode(parent, cache, key, markup) {
            const cached = cache.get(key);
            if (cached &&
                cached.markup === markup &&
                cached.el.isConnected &&
                cached.el.parentElement === parent) {
                return cached.el;
            }

            const el = htmlToElement(rawHtml(markup.trim()));
            cache.set(key, { markup, el });
            return el;
        }

        /**
         * Move/insert `desiredChildren` into `parent` in order, touching the
         * DOM only where it already differs, and remove any other children.
         * The infinite-scroll sentinel is left in place because
         * ensureScrollObserver() re-appends it as the last child afterwards.
         */
        function reconcileListChildren(parent, desiredChildren) {
            let cursor = parent.firstElementChild;

            for (const node of desiredChildren) {
                if (cursor === node) {
                    cursor = cursor.nextElementSibling;
                    continue;
                }
                parent.insertBefore(node, cursor);
            }

            while (cursor) {
                const next = cursor.nextElementSibling;
                if (cursor.id !== 'libraryScrollSentinel') {
                    cursor.remove();
                }
                cursor = next;
            }
        }

        function renderFileList() {
            const fileList = document.getElementById('fileList');

            // Restore/close any open action menu (which may be portaled to
            // <body>) before the list's DOM is rebuilt below.
            closeAllDropdowns();

            // Prune selections for files in *loaded* folders; leave selections in
            // unloaded folders alone so the user doesn't lose them.
            const loadedFiles = getAllLoadedFiles();
            const loadedFolderPaths = new Set();
            for (const [k, v] of folderFiles) {
                if (v && v.status === 'loaded') loadedFolderPaths.add(k);
            }
            const currentFilePaths = new Set(loadedFiles.map(f => f.relative_path));
            for (const filepath of Array.from(selectedFiles)) {
                const dir = getFolderForRelativePath(filepath);
                if (loadedFolderPaths.has(dir) && !currentFilePaths.has(filepath)) {
                    selectedFiles.delete(filepath);
                }
            }

            if (folderList.length === 0) {
                if (folderLoading) {
                    renderHtml(fileList, html`
                        <div class="loading">
                            <div class="spinner"></div>
                            <p>Loading files...</p>
                        </div>
                    `);
                } else if (searchQuery || filterMode !== 'all') {
                    renderHtml(fileList, html`
                        <div class="empty-state">
                            <div class="empty-state-icon">${uiIcon('search')}</div>
                            <h2>No matching files found</h2>
                            <p>Try a different search term or filter</p>
                        </div>
                    `);
                } else {
                    renderHtml(fileList, html`
                        <div class="empty-state">
                            <div class="empty-state-icon">${uiIcon('folder')}</div>
                            <h2>No comic files found</h2>
                            <p>Add some .cbz or .cbr files to your watched directory</p>
                        </div>
                    `);
                }
                updateSelectInfo();
                updateSelectAllCheckbox();
                ensureScrollObserver();
                return;
            }

            // Incremental rendering.
            //
            // Assigning the whole list in one `innerHTML` write forces the
            // browser to re-parse and re-lay-out every folder and every file
            // row. renderFileList() runs on every folder fetch completing,
            // every selection change and every infinite-scroll page append, so
            // with hundreds of folders loaded that full rebuild dominates the
            // frame budget and is the main source of jank in the library view.
            //
            // Instead, each folder is rendered into its own `.folder-section`
            // wrapper and the markup used to build it is remembered. On the
            // next render a folder whose generated markup is byte-identical
            // keeps its existing DOM node untouched, so only folders that
            // actually changed are re-parsed. Using the generated markup itself
            // as the cache key makes the comparison exact by construction:
            // there is no hand-maintained list of fields that could drift out
            // of sync with the template.

            // Bring the expand/collapse-all state up to date before its icon
            // is drawn from it.
            updateToggleAllButton();
            const headerHtml = `
                <div class="file-list-header">
                    <input type="checkbox" id="selectAll" onchange="toggleSelectAll(this.checked)" title="Select all loaded files">
                    <button class="toggle-all-btn" onclick="toggleAllFolders()" id="toggleAllBtn" title="Expand/Collapse All">
                        ${allFoldersExpanded ? '▼' : '▶'}
                    </button>
                    <div>File</div>
                    <div>Size</div>
                    <div>Modified</div>
                    <div>Actions</div>
                </div>
            `;

            // Desired child order for `.file-list`, in order.
            const desiredChildren = [];
            desiredChildren.push(reuseOrBuildListNode(fileList, fileListHeaderCache, 'header', headerHtml));

            const renderedFolderKeys = new Set();

            folderList.forEach(folder => {
                const dir = folder.path || '';
                let html = '';
                const isCollapsed = collapsedDirectories.has(dir);
                const entry = folderFiles.get(dir);
                const loadedFiles = (entry && entry.status === 'loaded') ? entry.files : [];

                if (dir) {
                    const allSelected = loadedFiles.length > 0 && loadedFiles.every(f => selectedFiles.has(f.relative_path));
                    const someSelected = loadedFiles.some(f => selectedFiles.has(f.relative_path));
                    html += `
                        <div class="directory-header">
                            <input type="checkbox"
                                   class="directory-checkbox"
                                   ${allSelected ? 'checked' : ''}
                                   ${someSelected && !allSelected ? 'style="opacity: 0.5"' : ''}
                                   onchange="toggleDirectorySelection('${escapeJs(dir)}', this.checked)"
                                   onclick="event.stopPropagation()">
                            <button class="directory-toggle-btn" onclick="toggleDirectory('${escapeJs(dir)}')">
                                <span class="directory-toggle ${isCollapsed ? 'collapsed' : ''}">▼</span>
                            </button>
                            <div class="directory-name-section" onclick="toggleDirectory('${escapeJs(dir)}')">
                                <span class="directory-icon">📁</span>
                                <span class="directory-path">${escapeHtml(dir)}</span>
                            </div>
                            <span class="directory-file-count">${folder.file_count} file${folder.file_count !== 1 ? 's' : ''}</span>
                            <span></span>
                            <span></span>
                        </div>
                    `;
                }

                html += `<div class="directory-content ${isCollapsed ? 'collapsed' : ''}" data-dir="${escapeHtml(dir)}">`;

                if (!isCollapsed) {
                    if (entry && entry.status === 'loaded') {
                        loadedFiles.forEach(file => {
                            html += renderFileRow(file, dir);
                        });
                    } else if (entry && entry.status === 'loading') {
                        html += `
                            <div class="folder-loading">
                                <div class="spinner"></div>
                                <span>Loading files...</span>
                            </div>
                        `;
                    } else if (entry && entry.status === 'error') {
                        html += `
                            <div class="folder-error">
                                Failed to load files in this folder.
                                <button class="btn btn-small" onclick="ensureFolderLoaded('${escapeJs(dir)}', true)">Retry</button>
                            </div>
                        `;
                    }
                }

                html += `</div>`;

                const sectionHtml = `<div class="folder-section">${html}</div>`;
                desiredChildren.push(reuseOrBuildListNode(fileList, folderSectionCache, dir, sectionHtml));
                renderedFolderKeys.add(dir);
            });

            // Drop cache entries for folders that are no longer listed (e.g.
            // after a filter/search change) so the map cannot grow unbounded.
            for (const key of Array.from(folderSectionCache.keys())) {
                if (!renderedFolderKeys.has(key)) {
                    folderSectionCache.delete(key);
                }
            }

            reconcileListChildren(fileList, desiredChildren);

            updateSelectInfo();
            updateSelectAllCheckbox();
            ensureScrollObserver();
        }
        
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
            
            // Show relative time for recent files
            if (diffDays === 0) {
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
            
            // Show date for older files
            const year = date.getFullYear();
            const month = String(date.getMonth() + 1).padStart(2, '0');
            const day = String(date.getDate()).padStart(2, '0');
            
            // Show year only if different from current year
            if (year !== now.getFullYear()) {
                return `${year}-${month}-${day}`;
            }
            return `${month}-${day}`;
        }
        

        // Encode a folder path for use in URLs (URL-safe base64, matching the
        // server-side DecodeBase64UrlSafe helper).
        function encodeFolderPath(p) {
            try {
                const b64 = btoa(unescape(encodeURIComponent(p || '')));
                return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
            } catch (e) {
                return '';
            }
        }

        function getAllLoadedFiles() {
            const out = [];
            for (const [, v] of folderFiles) {
                if (v && v.status === 'loaded' && Array.isArray(v.files)) {
                    out.push(...v.files);
                }
            }
            return out;
        }

        function getCurrentSelectableFilePaths() {
            if (libraryViewMode === 'series' && currentSeriesDetailId) {
                const cached = seriesIssuesCache.get(currentSeriesDetailId);
                if (!cached || !Array.isArray(cached.issues)) {
                    return [];
                }

                return cached.issues
                    .map(issue => issue.file_path)
                    .filter(path => typeof path === 'string' && path.length > 0);
            }

            return getAllLoadedFiles()
                .map(file => file.relative_path)
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

        function getFolderForRelativePath(relativePath) {
            if (!relativePath) return '';
            const idx = Math.max(relativePath.lastIndexOf('/'), relativePath.lastIndexOf('\\'));
            return idx >= 0 ? relativePath.substring(0, idx) : '';
        }

        function invalidateFolderForFile(relativePath) {
            const dir = getFolderForRelativePath(relativePath);
            const entry = folderFiles.get(dir);
            if (!entry) return;
            const wasExpanded = !collapsedDirectories.has(dir) && entry.status === 'loaded';
            folderFiles.delete(dir);
            if (wasExpanded) {
                ensureFolderLoaded(dir, true).catch(() => {});
            }
        }

        // The search, filter and sort folder contents are listed for, as the
        // query string of a folder's files request.
        function folderFilesQuery() {
            const params = [];
            if (searchQuery) params.push(`search=${encodeURIComponent(searchQuery)}`);
            if (filterMode !== 'all') params.push(`filter=${encodeURIComponent(filterMode)}`);
            if (sortMode !== 'name') params.push(`sort=${encodeURIComponent(sortMode)}`);
            if (sortDirection !== 'asc') params.push(`direction=${encodeURIComponent(sortDirection)}`);
            return params.join('&');
        }

        async function ensureFolderLoaded(dir, force = false) {
            const query = folderFilesQuery();
            const existing = folderFiles.get(dir);
            // Files listed for another filter, search or sort are stale.
            const sameQuery = !!existing && existing.query === query;
            if (!force && sameQuery && existing.status === 'loaded') return;
            // A forced load is for a changed filter, search or sort, so it
            // supersedes a request still in flight instead of joining it.
            if (!force && sameQuery && existing.status === 'loading') return existing.promise;

            // The root folder's path is empty, and an empty path segment
            // (folders//files) routes nowhere, so the root has its own URL.
            let url = apiUrl(dir
                ? `/api/files/folders/${encodeFolderPath(dir)}/files`
                : '/api/files/folders/files');
            if (query) url += '?' + query;

            const entry = { status: 'loading', files: [], promise: null, query };
            folderFiles.set(dir, entry);
            entry.promise = (async () => {
                let result;
                try {
                    const response = await fetch(url, { headers: getAuthHeaders() });
                    if (handleAuthError(response)) {
                        result = { status: 'error', files: [], query };
                    } else if (!response.ok) {
                        throw new Error(`HTTP error! status: ${response.status}`);
                    } else {
                        const data = await response.json();
                        result = { status: 'loaded', files: data.files || [], query };
                    }
                } catch (err) {
                    result = { status: 'error', files: [], query };
                }
                // A newer request for this folder has taken over: an older
                // answer must not overwrite the latest one, so wait for it.
                const current = folderFiles.get(dir);
                if (current && current !== entry) {
                    return current.promise;
                }
                folderFiles.set(dir, result);
                renderFileList();
            })();

            renderFileList();
            return entry.promise;
        }

        function ensureScrollObserver() {
            if (scrollObserver) {
                try { scrollObserver.disconnect(); } catch (e) { /* ignore */ }
            }
            const fileList = document.getElementById('fileList');
            if (!fileList) return;
            // The sentinel must live INSIDE the scrolling container (`.file-list`)
            // and be observed against that container as the IO root. Otherwise the
            // sentinel — sitting at the bottom of the viewport because `.file-list`
            // has `flex: 1` — would be perpetually within the 400 px rootMargin and
            // trigger an infinite "load more → re-render → re-observe" loop. That
            // loop is what made the list flicker and items unclickable on desktop
            // and could leave the list appearing cut off or never displayed at all
            // on smaller viewports.
            let sentinel = document.getElementById('libraryScrollSentinel');
            if (!sentinel) {
                sentinel = document.createElement('div');
                sentinel.id = 'libraryScrollSentinel';
            }
            if (sentinel.parentElement !== fileList) {
                fileList.appendChild(sentinel);
            } else if (sentinel !== fileList.lastElementChild) {
                fileList.appendChild(sentinel);
            }
            scrollObserver = new IntersectionObserver((entries) => {
                for (const e of entries) {
                    if (!e.isIntersecting) continue;
                    if (libraryViewMode === 'series') {
                        if (!seriesLoading && seriesOffset < seriesTotal) {
                            loadSeriesLibrary({ append: true });
                        }
                    } else {
                        if (!folderLoading && folderOffset < folderTotal) {
                            loadFolders({ append: true });
                        }
                    }
                }
            }, { root: fileList, rootMargin: '400px' });
            scrollObserver.observe(sentinel);
        }


        // Extract just the filename portion from a relative or absolute path.
        function extractDisplayName(filepath) {
            if (!filepath) return '';
            const parts = filepath.split(/[/\\]/);
            return parts[parts.length - 1] || filepath;
        }
        
        function truncateFilenameMiddle(filename) {
            // Split filename into start and end parts for middle truncation
            // This ensures the file extension is always visible
            if (filename.length <= FILENAME_TRUNCATION_START_LENGTH + FILENAME_TRUNCATION_END_LENGTH) {
                // Filename is short enough, no truncation needed
                return {
                    start: filename,
                    end: ''
                };
            }
            
            // Find the last dot for extension
            const lastDotIndex = filename.lastIndexOf('.');
            const hasExtension = lastDotIndex > 0 && lastDotIndex > filename.length - MAX_EXTENSION_LENGTH;
            
            if (hasExtension) {
                // Preserve extension
                const extension = filename.substring(lastDotIndex);
                const nameWithoutExt = filename.substring(0, lastDotIndex);
                
                // Calculate how much of the name we can show
                const remainingForName = FILENAME_TRUNCATION_END_LENGTH - extension.length;
                
                if (nameWithoutExt.length <= FILENAME_TRUNCATION_START_LENGTH + remainingForName) {
                    // Can show the whole name
                    return {
                        start: nameWithoutExt,
                        end: extension
                    };
                }
                
                // Need to truncate
                const start = nameWithoutExt.substring(0, FILENAME_TRUNCATION_START_LENGTH);
                const end = nameWithoutExt.substring(nameWithoutExt.length - remainingForName) + extension;
                
                return {
                    start: start + TRUNCATION_ELLIPSIS,
                    end: end
                };
            } else {
                // No clear extension, just split at character count
                const start = filename.substring(0, FILENAME_TRUNCATION_START_LENGTH);
                const end = filename.substring(filename.length - FILENAME_TRUNCATION_END_LENGTH);
                
                return {
                    start: start + TRUNCATION_ELLIPSIS,
                    end: end
                };
            }
        }
        
        function getDropdownId(filepath) {
            // Generate a consistent dropdown ID from filepath
            // This must match the ID used in the HTML generation
            return 'dropdown-' + filepath.replace(/[^a-zA-Z0-9]/g, '_');
        }
        
        function toggleSelectAll(checked) {
            const visibleFilePaths = getCurrentSelectableFilePaths();
            if (checked) {
                visibleFilePaths.forEach(filepath => selectedFiles.add(filepath));
            } else {
                visibleFilePaths.forEach(filepath => selectedFiles.delete(filepath));
            }
            if (libraryViewMode === 'series' && currentSeriesDetailId) {
                renderSeriesDetail(currentSeriesDetailId);
            } else {
                renderFileList();
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
            if (libraryViewMode === 'series' && !currentSeriesDetailId && selectedSeries.size > 0) {
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

        async function toggleDirectory(dir) {
            const wasCollapsed = collapsedDirectories.has(dir);
            if (wasCollapsed) {
                collapsedDirectories.delete(dir);
                renderFileList();
                await ensureFolderLoaded(dir);
            } else {
                collapsedDirectories.add(dir);
                renderFileList();
            }
            updateToggleAllButton();
        }

        async function toggleAllFolders() {
            if (allFoldersExpanded) {
                collapseAllFolders();
            } else {
                await expandAllFolders();
            }
        }

        async function expandAllFolders() {
            collapsedDirectories.clear();
            allFoldersExpanded = true;
            renderFileList();

            // Concurrency-limited loader for unloaded folders and for folders
            // whose files were listed for another filter, search or sort.
            const concurrency = 4;
            const query = folderFilesQuery();
            const queue = folderList
                .map(f => f.path || '')
                .filter(dir => {
                    const e = folderFiles.get(dir);
                    return !e || e.status === 'error' || e.query !== query;
                });
            let active = 0;
            let index = 0;
            await new Promise(resolve => {
                const launch = () => {
                    while (active < concurrency && index < queue.length) {
                        const dir = queue[index++];
                        active++;
                        ensureFolderLoaded(dir).catch(() => {}).finally(() => {
                            active--;
                            if (index >= queue.length && active === 0) {
                                resolve();
                            } else {
                                launch();
                            }
                        });
                    }
                    if (queue.length === 0) resolve();
                };
                launch();
            });
            updateToggleAllButton();
        }

        function collapseAllFolders() {
            // The root has no header to reopen it by, so it stays open.
            collapsedDirectories = new Set(folderList.map(f => f.path || '').filter(dir => dir));
            allFoldersExpanded = false;
            renderFileList();
        }

        function updateToggleAllButton() {
            // Only folders with a header can be collapsed; the root can't.
            // Folders no longer listed can still be marked collapsed, so only
            // the listed ones are counted.
            const dirs = folderList.filter(f => f.path);
            const collapsedDirs = dirs.filter(f => collapsedDirectories.has(f.path)).length;
            if (dirs.length > 0 && collapsedDirs >= dirs.length) {
                allFoldersExpanded = false;
            } else {
                allFoldersExpanded = true;
            }
        }

        async function toggleDirectorySelection(dir, checked) {
            const entry = folderFiles.get(dir);
            if (!entry || entry.status !== 'loaded' || entry.query !== folderFilesQuery()) {
                await ensureFolderLoaded(dir);
            }
            const refreshed = folderFiles.get(dir);
            const dirFiles = (refreshed && refreshed.status === 'loaded') ? refreshed.files : [];
            dirFiles.forEach(file => {
                if (checked) {
                    selectedFiles.add(file.relative_path);
                } else {
                    selectedFiles.delete(file.relative_path);
                }
            });
            renderFileList();
        }
