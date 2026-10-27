        // ComicMaintainer SPA — per-file modals: file info, duplicate review, series folders, combine folders, tag editing.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        function showFileInfo(filepath, filename) {
            // Show file information in a modal
            document.getElementById('fileInfoPath').textContent = filepath;
            document.getElementById('fileInfoName').textContent = filename;
            
            // Find the file in the loaded files to get size info
            const file = getAllLoadedFiles().find(f => f.relative_path === filepath);
            const duplicateReview = document.getElementById('fileInfoDuplicateReview');
            const duplicateReviewBtn = document.getElementById('fileInfoDuplicateReviewBtn');
            if (file) {
                document.getElementById('fileInfoSize').textContent = formatFileSize(file.size);
                document.getElementById('fileInfoProcessed').textContent = file.processed ? '✅ Yes (Renamed & Normalized)' : '⚠️ No';
                document.getElementById('fileInfoRenamed').textContent = file.renamed ? '🔵 Yes' : 'No';
                document.getElementById('fileInfoNormalized').textContent = file.normalized ? '🔴 Yes' : 'No';
                document.getElementById('fileInfoDuplicate').textContent = file.duplicate ? '🔁 Yes' : 'No';
                if (duplicateReview && duplicateReviewBtn) {
                    duplicateReview.style.display = file.duplicate ? 'block' : 'none';
                    if (file.duplicate) {
                        duplicateReviewBtn.dataset.filepath = filepath;
                    } else {
                        delete duplicateReviewBtn.dataset.filepath;
                    }
                }
            } else if (duplicateReview && duplicateReviewBtn) {
                duplicateReview.style.display = 'none';
                delete duplicateReviewBtn.dataset.filepath;
            }
            
            document.getElementById('fileInfoModal').classList.add('active');
        }
        
        function closeFileInfoModal() {
            document.getElementById('fileInfoModal').classList.remove('active');
        }

        function openDuplicateReviewFromFileInfo() {
            const duplicateReviewBtn = document.getElementById('fileInfoDuplicateReviewBtn');
            const filepath = duplicateReviewBtn?.dataset.filepath;
            if (!filepath) {
                return;
            }

            closeFileInfoModal();
            openDuplicateReviewModal(filepath);
        }

        // Open the duplicate review modal and optionally focus a specific duplicate file when provided.
        async function openDuplicateReviewModal(initialFilepath = null) {
            const loadId = ++duplicateReviewLoadId;
            const modal = document.getElementById('duplicateReviewModal');
            const emptyState = document.getElementById('duplicateReviewEmptyState');
            const content = document.getElementById('duplicateReviewContent');

            duplicateReviewFiles = [];
            duplicateReviewIndex = 0;
            modal.classList.add('active');
            emptyState.style.display = 'none';
            emptyState.textContent = 'No duplicate files are waiting for review.';
            content.style.display = 'block';
            document.getElementById('duplicateReviewName').textContent = 'Loading duplicates...';
            document.getElementById('duplicateReviewPath').textContent = '';
            updateDuplicateReviewNavigation();

            try {
                const response = await fetch(apiUrl('/api/files?filter=duplicates&per_page=-1&sort=name'), {
                    headers: getAuthHeaders()
                });
                if (loadId !== duplicateReviewLoadId) return;
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }

                const data = await response.json();
                if (loadId !== duplicateReviewLoadId) return;
                duplicateReviewFiles = Array.isArray(data.files) ? data.files : [];

                if (duplicateReviewFiles.length === 0) {
                    emptyState.style.display = 'block';
                    content.style.display = 'none';
                    updateDuplicateReviewNavigation();
                    return;
                }

                const matchedIndex = initialFilepath
                    ? duplicateReviewFiles.findIndex(file => file.relative_path === initialFilepath)
                    : -1;
                duplicateReviewIndex = matchedIndex >= 0 ? matchedIndex : 0;
                renderDuplicateReviewItem();
            } catch (error) {
                if (loadId !== duplicateReviewLoadId) return;
                console.error('Failed to load duplicates for review:', error);
                emptyState.style.display = 'block';
                emptyState.textContent = `Failed to load duplicates: ${error.message}`;
                content.style.display = 'none';
                updateDuplicateReviewNavigation();
            }
        }

        function closeDuplicateReviewModal() {
            duplicateReviewLoadId++;
            document.getElementById('duplicateReviewModal').classList.remove('active');
        }

        function updateDuplicateReviewNavigation() {
            const total = duplicateReviewFiles.length;
            const counter = document.getElementById('duplicateReviewCounter');
            const prevBtn = document.getElementById('duplicateReviewPrevBtn');
            const nextBtn = document.getElementById('duplicateReviewNextBtn');
            const deleteAllBtn = document.getElementById('duplicateReviewDeleteAllBtn');

            counter.textContent = total > 0 ? `${duplicateReviewIndex + 1} of ${total}` : '0 of 0';
            prevBtn.disabled = total === 0 || duplicateReviewIndex <= 0;
            nextBtn.disabled = total === 0 || duplicateReviewIndex >= total - 1;
            if (deleteAllBtn) {
                deleteAllBtn.disabled = total === 0;
                deleteAllBtn.textContent = total > 0
                    ? `🗑️ Delete All ${total} Duplicates`
                    : '🗑️ Delete All Duplicates';
            }
        }

        function renderDuplicateReviewItem() {
            const emptyState = document.getElementById('duplicateReviewEmptyState');
            const content = document.getElementById('duplicateReviewContent');
            const file = duplicateReviewFiles[duplicateReviewIndex];

            if (!file) {
                emptyState.style.display = 'block';
                content.style.display = 'none';
                updateDuplicateReviewNavigation();
                return;
            }

            emptyState.style.display = 'none';
            content.style.display = 'block';

            document.getElementById('duplicateReviewName').textContent = file.name;
            document.getElementById('duplicateReviewPath').textContent = file.relative_path;
            document.getElementById('duplicateReviewSize').textContent = formatFileSize(file.size);
            document.getElementById('duplicateReviewModified').textContent = formatModifiedDate(file.modified);
            document.getElementById('duplicateReviewReadStatus').textContent = file.read ? '👁️ Read' : '📚 Unread';
            document.getElementById('duplicateReviewReadToggleBtn').textContent = file.read ? '📚 Mark Unread' : '✅ Mark Read';

            updateDuplicateReviewNavigation();
        }

        function changeDuplicateReviewItem(direction) {
            const nextIndex = duplicateReviewIndex + direction;
            if (nextIndex < 0 || nextIndex >= duplicateReviewFiles.length) {
                return;
            }

            duplicateReviewIndex = nextIndex;
            renderDuplicateReviewItem();
        }

        function getCurrentDuplicateReviewFile() {
            return duplicateReviewFiles[duplicateReviewIndex] || null;
        }

        function openDuplicateInfo() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            closeDuplicateReviewModal();
            showFileInfo(file.relative_path, file.name);
        }

        function openDuplicateTags() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            closeDuplicateReviewModal();
            viewTags(file.relative_path);
        }

        function openDuplicateReader() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            closeDuplicateReviewModal();
            readComic(file.relative_path);
        }

        async function toggleDuplicateReadStatus() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            if (file.read) {
                await markFileUnread(file.relative_path);
            } else {
                await markFileRead(file.relative_path);
            }

            await openDuplicateReviewModal(file.relative_path);
        }

        async function deleteCurrentDuplicate() {
            const file = getCurrentDuplicateReviewFile();
            if (!file) return;

            await deleteSingleFile(file.relative_path);
            await openDuplicateReviewModal();
        }

        // Delete every file currently marked as a duplicate in one background
        // job instead of stepping through them one-by-one.
        async function deleteAllDuplicates() {
            const paths = duplicateReviewFiles
                .map(file => file.relative_path)
                .filter(path => typeof path === 'string' && path.length > 0);

            if (paths.length === 0) {
                showMessage('No duplicates to delete', 'error');
                return;
            }
            if (!canStartJob()) return;

            if (!confirm(`Are you sure you want to delete all ${paths.length} file(s) marked as duplicates?\n\nThis action cannot be undone!`)) {
                return;
            }

            closeDuplicateReviewModal();
            showProgressModal(`Deleting ${paths.length} duplicate(s)...`);

            try {
                const response = await fetch(apiUrl('/api/jobs/delete-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: paths })
                });

                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }

                if (!response.ok) {
                    const errorData = await response.json().catch(() => ({}));
                    throw new Error(errorData.error || `Failed to start delete job (HTTP ${response.status})`);
                }

                const data = await response.json();
                const jobId = data.jobId || data.job_id;
                const totalItems = data.totalItems || data.total_items || paths.length;

                duplicateReviewFiles = [];
                duplicateReviewIndex = 0;

                showMessage(`Deleting ${totalItems} duplicate(s) in background`, 'info');
                await trackJobStatus(jobId, `Deleting ${totalItems} duplicate(s)...`);
            } catch (error) {
                console.error('[DUPLICATES] Error starting delete-all job:', error);
                showMessage('Failed to start delete: ' + error.message, 'error');
                closeProgressModal();
            }
        }

        // Per-series folder list state. Holds the latest response from
        // /api/files/series/{id}/folders so the "Merge Folders" button can
        // hand off to the existing combine-folders modal without re-fetching.
        let seriesFoldersCurrent = null;

        async function openSeriesFoldersModal(seriesId, seriesTitle) {
            if (!seriesId) return;
            const modal = document.getElementById('seriesFoldersModal');
            const list = document.getElementById('seriesFoldersList');
            const emptyState = document.getElementById('seriesFoldersEmptyState');
            const mergeBtn = document.getElementById('seriesFoldersMergeBtn');
            const mergeNote = document.getElementById('seriesFoldersMergeNote');
            const titleEl = document.getElementById('seriesFoldersModalTitle');

            titleEl.textContent = seriesTitle ? `Folders for "${seriesTitle}"` : 'Series Folders';
            renderHtml(list, html`<div class="loading"><div class="spinner"></div><p>Loading folders...</p></div>`);
            emptyState.style.display = 'none';
            mergeBtn.disabled = true;
            mergeNote.style.display = 'none';
            mergeNote.textContent = '';
            seriesFoldersCurrent = null;
            modal.classList.add('active');

            try {
                const response = await fetch(apiUrl(`/api/files/series/${encodeURIComponent(seriesId)}/folders`), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                seriesFoldersCurrent = data;
                const folders = Array.isArray(data.folders) ? data.folders : [];

                if (folders.length === 0) {
                    clearChildren(list);
                    emptyState.style.display = 'block';
                    return;
                }

                renderHtml(list, folders.map(folder => html`
                    <div class="combine-folder-row" style="padding: 8px 0; border-bottom: 1px solid var(--border, #ddd);">
                        <div style="font-weight: 500; word-break: break-all;">${folder.directory}</div>
                        <div class="combine-folders-meta">${folder.file_count} file${folder.file_count === 1 ? '' : 's'} · ${formatFileSize(folder.total_size)}</div>
                    </div>
                `));

                if (folders.length < 2) {
                    mergeBtn.disabled = true;
                    mergeNote.style.display = 'block';
                    mergeNote.textContent = 'This series lives in a single folder — nothing to merge.';
                } else if (data.combine_group_key) {
                    mergeBtn.disabled = false;
                    mergeNote.style.display = 'block';
                    mergeNote.textContent = 'Click "Merge Folders" to combine these folders into one on disk.';
                } else {
                    // The API didn't return a combine group key for some
                    // reason (older server, unexpected error). Fall back to
                    // the global combine flow rather than blocking the user.
                    mergeBtn.disabled = true;
                    mergeNote.style.display = 'block';
                    mergeNote.textContent = 'Use the global "Combine Folders" tool from the toolbar to merge these folders.';
                }
            } catch (error) {
                console.error('Failed to load series folders:', error);
                clearChildren(list);
                emptyState.style.display = 'block';
                emptyState.textContent = `Failed to load folders: ${error.message}`;
            }
        }

        function closeSeriesFoldersModal() {
            document.getElementById('seriesFoldersModal').classList.remove('active');
            seriesFoldersCurrent = null;
        }

        async function openMergeForSeriesFolders() {
            if (!seriesFoldersCurrent || !seriesFoldersCurrent.combine_group_key) {
                return;
            }
            const targetGroupKey = seriesFoldersCurrent.combine_group_key;
            const suggestedDestination = seriesFoldersCurrent.suggested_destination_directory || null;
            // Hand off to the existing combine-folders modal, then jump to the
            // group that corresponds to this series so the user can review and
            // confirm using the same UI as the global combine flow.
            closeSeriesFoldersModal();
            await openCombineFoldersModal();
            const matchIndex = combineFolderGroups.findIndex(g => g && g.groupKey === targetGroupKey);
            if (matchIndex >= 0) {
                combineFolderIndex = matchIndex;
                combineFolderSelectedDestination = suggestedDestination;
                renderCombineFolderGroup();
                updateCombineFoldersNavigation();
            }
        }

        async function openCombineFoldersModal() {
            const modal = document.getElementById('combineFoldersModal');
            const emptyState = document.getElementById('combineFoldersEmptyState');
            const content = document.getElementById('combineFoldersContent');

            modal.classList.add('active');
            emptyState.style.display = 'none';
            emptyState.textContent = 'No folders look combinable right now.';
            content.style.display = 'block';
            document.getElementById('combineFoldersGroupTitle').textContent = 'Loading combinable folders...';
            document.getElementById('combineFoldersGroupMeta').textContent = '';
            document.getElementById('combineFoldersSuggestion').textContent = '';
            clearChildren(document.getElementById('combineFoldersList'));
            document.getElementById('combineFoldersPreview').style.display = 'none';
            clearChildren(document.getElementById('combineFoldersPreview'));
            document.getElementById('combineFoldersPreviewBtn').disabled = true;
            document.getElementById('combineFoldersConfirmBtn').disabled = true;

            try {
                const response = await fetch(apiUrl('/api/files/combinable-folders'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const data = await response.json();
                combineFolderGroups = Array.isArray(data.groups) ? data.groups : [];
                combineFolderIndex = 0;
                combineFolderSelectedDestination = null;

                if (combineFolderGroups.length === 0) {
                    emptyState.style.display = 'block';
                    content.style.display = 'none';
                    updateCombineFoldersNavigation();
                    return;
                }

                renderCombineFolderGroup();
            } catch (error) {
                console.error('Failed to load combinable folders:', error);
                emptyState.style.display = 'block';
                emptyState.textContent = `Failed to load combinable folders: ${error.message}`;
                content.style.display = 'none';
                updateCombineFoldersNavigation();
            }
        }

        function closeCombineFoldersModal() {
            document.getElementById('combineFoldersModal').classList.remove('active');
        }

        function updateCombineFoldersNavigation() {
            const total = combineFolderGroups.length;
            const counter = document.getElementById('combineFoldersCounter');
            const prevBtn = document.getElementById('combineFoldersPrevBtn');
            const nextBtn = document.getElementById('combineFoldersNextBtn');

            counter.textContent = total > 0 ? `${combineFolderIndex + 1} of ${total}` : '0 of 0';
            prevBtn.disabled = total === 0 || combineFolderIndex <= 0;
            nextBtn.disabled = total === 0 || combineFolderIndex >= total - 1;
        }

        function changeCombineFolderGroup(direction) {
            const next = combineFolderIndex + direction;
            if (next < 0 || next >= combineFolderGroups.length) {
                return;
            }
            combineFolderIndex = next;
            combineFolderSelectedDestination = null;
            renderCombineFolderGroup();
        }

        function getCurrentCombineFolderGroup() {
            return combineFolderGroups[combineFolderIndex] || null;
        }

        function formatCombineFolderDate(value) {
            if (!value) return '—';
            try {
                const d = new Date(value);
                if (isNaN(d.getTime())) return '—';
                return d.toLocaleString();
            } catch (e) {
                return '—';
            }
        }

        function renderCombineFolderGroup() {
            const group = getCurrentCombineFolderGroup();
            const emptyState = document.getElementById('combineFoldersEmptyState');
            const content = document.getElementById('combineFoldersContent');
            const previewPanel = document.getElementById('combineFoldersPreview');

            previewPanel.style.display = 'none';
            clearChildren(previewPanel);

            if (!group) {
                emptyState.style.display = 'block';
                content.style.display = 'none';
                document.getElementById('combineFoldersPreviewBtn').disabled = true;
                document.getElementById('combineFoldersConfirmBtn').disabled = true;
                updateCombineFoldersNavigation();
                return;
            }

            emptyState.style.display = 'none';
            content.style.display = 'block';

            const title = group.volume
                ? `${group.seriesName} · Vol. ${group.volume}`
                : group.seriesName;
            document.getElementById('combineFoldersGroupTitle').textContent = title || 'Combinable folders';
            document.getElementById('combineFoldersGroupMeta').textContent =
                `${group.folders.length} folder${group.folders.length === 1 ? '' : 's'} · ${group.totalFileCount} file${group.totalFileCount === 1 ? '' : 's'}`;
            document.getElementById('combineFoldersSuggestion').textContent = group.suggestionReason || '';

            if (!combineFolderSelectedDestination) {
                combineFolderSelectedDestination = group.suggestedDestinationDirectory;
            }
            // Ensure selection is part of the group; otherwise reset to suggested.
            if (!group.folders.some(f => f.directory === combineFolderSelectedDestination)) {
                combineFolderSelectedDestination = group.suggestedDestinationDirectory;
            }

            const list = document.getElementById('combineFoldersList');
            clearChildren(list);
            group.folders.forEach((folder, idx) => {
                const isSelected = folder.directory === combineFolderSelectedDestination;
                const isSuggested = folder.directory === group.suggestedDestinationDirectory;
                const card = document.createElement('div');
                card.className = 'combine-folder-card'
                    + (isSelected ? ' selected' : '')
                    + (isSuggested ? ' suggested' : '');
                card.setAttribute('role', 'button');
                card.tabIndex = 0;

                const radioId = `combineFolderRadio_${combineFolderIndex}_${idx}`;
                const sample = (folder.sampleFileNames || []).slice(0, 5);
                const sampleMarkup = sample.length
                    ? html`<div class="combine-folder-files">Sample files:<ul>${sample.map(n => html`<li>${n}</li>`)}</ul></div>`
                    : '';

                renderHtml(card, html`
                    <div class="combine-folder-card-header">
                        <input type="radio" name="combineFolderDestination" id="${radioId}" ${isSelected ? rawHtml('checked') : ''}>
                        <label for="${radioId}" class="combine-folder-path">${folder.directory}</label>
                    </div>
                    <div class="combine-folder-stats">
                        <div>Files <strong>${folder.fileCount.toLocaleString()}</strong></div>
                        <div>Total size <strong>${formatFileSize(folder.totalSize)}</strong></div>
                        <div>Newest file <strong>${formatCombineFolderDate(folder.newestFileAddedAt)}</strong></div>
                        <div>Oldest file <strong>${formatCombineFolderDate(folder.oldestFileAddedAt)}</strong></div>
                    </div>
                    ${sampleMarkup}
                `);

                const selectThis = () => {
                    combineFolderSelectedDestination = folder.directory;
                    renderCombineFolderGroup();
                };
                card.addEventListener('click', (ev) => {
                    if (ev.target && ev.target.tagName === 'A') return;
                    selectThis();
                });
                card.addEventListener('keydown', (ev) => {
                    if (ev.key === 'Enter' || ev.key === ' ') {
                        ev.preventDefault();
                        selectThis();
                    }
                });
                list.appendChild(card);
            });

            const canCombine = group.folders.length >= 2 && !!combineFolderSelectedDestination;
            document.getElementById('combineFoldersPreviewBtn').disabled = !canCombine || combineFolderActionInFlight;
            document.getElementById('combineFoldersConfirmBtn').disabled = !canCombine || combineFolderActionInFlight;
            updateCombineFoldersNavigation();
        }

        function buildCombineFoldersRequestBody() {
            const group = getCurrentCombineFolderGroup();
            if (!group || !combineFolderSelectedDestination) {
                return null;
            }
            const sources = group.folders
                .map(f => f.directory)
                .filter(d => d !== combineFolderSelectedDestination);
            return {
                groupKey: group.groupKey,
                destinationDirectory: combineFolderSelectedDestination,
                sourceDirectories: sources
            };
        }

        async function previewCombineFolders() {
            const body = buildCombineFoldersRequestBody();
            if (!body) return;
            const previewPanel = document.getElementById('combineFoldersPreview');
            previewPanel.style.display = 'block';
            renderHtml(previewPanel, html`<em>Building preview...</em>`);
            combineFolderActionInFlight = true;
            document.getElementById('combineFoldersPreviewBtn').disabled = true;
            document.getElementById('combineFoldersConfirmBtn').disabled = true;
            try {
                const response = await fetch(apiUrl('/api/files/combine-folders/preview'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(body)
                });
                if (handleAuthError(response)) return;
                const data = await response.json();
                if (!response.ok) {
                    throw new Error(data.error || `HTTP error! status: ${response.status}`);
                }
                const moves = Array.isArray(data.moves) ? data.moves : [];
                if (moves.length === 0) {
                    renderHtml(previewPanel, html`<em>No files will be moved.</em>`);
                } else {
                    const conflicts = moves.filter(m => m.conflict).length;
                    const skipped = moves.filter(m => m.skipped).length;
                    const items = moves.map(m => {
                        const cls = m.skipped ? 'skipped' : (m.conflict ? 'conflict' : '');
                        const note = m.skipped ? ' (skipped)' : (m.conflict ? ' (renamed to avoid conflict)' : '');
                        return html`<li class="${cls}">${m.sourcePath} → ${m.destinationPath}${note}</li>`;
                    });
                    renderHtml(previewPanel, html`
                        <h4>Move plan (${moves.length} file${moves.length === 1 ? '' : 's'}${conflicts ? `, ${conflicts} renamed` : ''}${skipped ? `, ${skipped} skipped` : ''})</h4>
                        <ul>${items}</ul>
                    `);
                }
            } catch (error) {
                console.error('Failed to build combine preview:', error);
                renderHtml(previewPanel, html`<span class="conflict">Failed to build preview: ${error.message}</span>`);
            } finally {
                combineFolderActionInFlight = false;
                renderCombineFolderGroup();
            }
        }

        async function confirmCombineFolders() {
            const body = buildCombineFoldersRequestBody();
            if (!body) return;
            if (!canStartJob()) return;
            const group = getCurrentCombineFolderGroup();
            const sourceCount = group ? group.folders.length - 1 : 0;
            const message = `Combine ${sourceCount} folder${sourceCount === 1 ? '' : 's'} into:\n${combineFolderSelectedDestination}\n\nFiles will be moved on disk. Continue?`;
            if (!confirm(message)) {
                return;
            }
            combineFolderActionInFlight = true;
            document.getElementById('combineFoldersPreviewBtn').disabled = true;
            document.getElementById('combineFoldersConfirmBtn').disabled = true;
            try {
                const response = await fetch(apiUrl('/api/files/combine-folders'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(body)
                });
                if (handleAuthError(response)) return;
                const data = await response.json();
                if (!response.ok) {
                    throw new Error(data.error || `HTTP error! status: ${response.status}`);
                }

                // The server now enqueues the moves as a background job and
                // returns 202 immediately with { jobId, totalItems, ... }.
                // Track via the standard SSE-driven progress modal so the user
                // sees per-move progress, then refresh the modal once the job
                // finishes.
                const jobId = data.jobId || data.job_id;
                const totalItems = data.totalItems || data.total_items || 0;
                const skipped = data.skipped || 0;

                if (!jobId || totalItems === 0) {
                    // Nothing to move (everything was skipped). Just refresh.
                    showMessage(`Combine complete: 0 moved, ${skipped} skipped, 0 failed.`, 'info');
                    if (typeof loadLibraryHealth === 'function') loadLibraryHealth();
                    if (typeof refreshFiles === 'function') refreshFiles();
                    await openCombineFoldersModal();
                    return;
                }

                showMessage(`Combining ${totalItems} file(s) in background${skipped > 0 ? ` (${skipped} skipped)` : ''}`, 'info');
                showProgressModal(`Combining ${totalItems} file(s)...`);
                await trackJobStatus(jobId, `Combining ${totalItems} file(s)...`);

                // The progress-modal completion handler already refreshes the
                // library view; refresh the combine-folders modal once the job
                // settles so the just-combined group disappears.
                if (typeof loadLibraryHealth === 'function') loadLibraryHealth();
                if (typeof refreshFiles === 'function') refreshFiles();
                await openCombineFoldersModal();
            } catch (error) {
                console.error('Failed to combine folders:', error);
                showMessage(`Failed to combine folders: ${error.message}`, 'error');
            } finally {
                combineFolderActionInFlight = false;
                renderCombineFolderGroup();
            }
        }
        
        async function viewTags(filepath) {
            try {
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/tags`), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                const tags = await response.json();
                
                if (tags.error) {
                    showMessage(tags.error, 'error');
                    return;
                }
                
                currentEditFile = filepath;
                
                // Populate form
                Object.keys(tags).forEach(key => {
                    const input = document.getElementById(key);
                    if (input) {
                        input.value = tags[key] || '';
                    }
                });
                
                document.getElementById('modalTitle').textContent = `Edit Tags - ${filepath}`;
                document.getElementById('tagModal').classList.add('active');
            } catch (error) {
                showMessage('Failed to load tags: ' + error.message, 'error');
            }
        }
        
        function closeModal() {
            document.getElementById('tagModal').classList.remove('active');
            currentEditFile = null;
        }
        
        async function saveTags() {
            if (!currentEditFile) return;
            // Refused before the dialog closes, so the edits are not lost.
            if (!canStartJob()) return;

            // Capture the target file path before calling closeModal(), which
            // resets currentEditFile to null. Without this snapshot, the
            // request body below would serialize Files:[null] and the server
            // would silently fail to update any file's metadata.
            const targetFile = currentEditFile;

            const form = document.getElementById('tagForm');
            const formData = new FormData(form);
            const metadata = {};
            
            // Only include non-empty fields. Empty inputs (e.g. an empty Year)
            // would fail JSON deserialization on the server because
            // ComicMetadata.Year is int? — sending "" would cause a 400 and
            // the file's tags would never get written.
            for (let [key, value] of formData.entries()) {
                if (typeof value !== 'string' || !value.trim()) continue;
                // Capitalize first letter to match ComicMetadata property names
                const propertyName = key.charAt(0).toUpperCase() + key.slice(1);
                if (propertyName === 'Year') {
                    const parsedYear = parseInt(value, 10);
                    if (!isNaN(parsedYear)) {
                        metadata[propertyName] = parsedYear;
                    }
                    continue;
                }
                metadata[propertyName] = value;
            }
            
            closeModal();
            showProgressModal('Updating metadata...');
            
            try {
                console.log('[SINGLE FILE] Starting update metadata file request...');
                // Start the update metadata job using job-based pattern
                const response = await fetch(apiUrl('/api/jobs/update-metadata-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: [targetFile], Metadata: metadata })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[SINGLE FILE] Failed to start updating metadata for file (HTTP ${response.status})`);
                    throw new Error('Failed to start update metadata job');
                }
                
                const result = await response.json();
                const jobId = result.job_id;
                
                if (!jobId) {
                    throw new Error('No job ID returned');
                }
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${targetFile}`);
                showMessage(`Started updating metadata for file in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Updating Metadata...');
                
            } catch (error) {
                console.error('[SINGLE FILE] Error starting update metadata for file:', error);
                showMessage('Failed to update metadata: ' + error.message, 'error');
                closeProgressModal();
            } finally {
                currentEditFile = null;
            }
        }
        
        function batchUpdateTags() {
            if (selectedFiles.size === 0) return;
            document.getElementById('batchModal').classList.add('active');
        }
        
        function closeBatchModal() {
            document.getElementById('batchModal').classList.remove('active');
            document.getElementById('batchForm').reset();
        }
        
        async function saveBatchTags() {
            const form = document.getElementById('batchForm');
            const formData = new FormData(form);
            const metadata = {};
            
            // Only include non-empty fields
            for (let [key, value] of formData.entries()) {
                if (value.trim()) {
                    // Capitalize first letter to match ComicMetadata property names
                    const propertyName = key.charAt(0).toUpperCase() + key.slice(1);
                    if (propertyName === 'Year') {
                        const parsedYear = parseInt(value, 10);
                        if (!isNaN(parsedYear)) {
                            metadata[propertyName] = parsedYear;
                        }
                        continue;
                    }
                    metadata[propertyName] = value;
                }
            }
            
            if (Object.keys(metadata).length === 0) {
                showMessage('Please enter at least one tag to update', 'error');
                return;
            }
            // Refused before the dialog closes, so the edits are not lost.
            if (!canStartJob()) return;
            
            closeBatchModal();
            showProgressModal('Updating metadata...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log('[BATCH] Starting update metadata selected files request...');
                // Start the job using the jobs endpoint
                const response = await fetch(apiUrl('/api/jobs/update-metadata-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: files, Metadata: metadata })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start updating metadata for selected files (HTTP ${response.status})`);
                    throw new Error('Failed to start update metadata job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} selected files`);
                showMessage(`Started updating metadata for ${totalItems} selected files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Updating Metadata for Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting update metadata for selected files:', error);
                showMessage('Failed to start updating metadata: ' + error.message, 'error');
                closeProgressModal();
            }
        }
