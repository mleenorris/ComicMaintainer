        // ComicMaintainer SPA — batch operations, job tracking/resume and the progress modal.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Job statuses a job can never leave. 'interrupted' means the server restarted while
        // the job was in flight; like the other terminal states it must stop polling, or the
        // UI would poll forever for a job that will never progress again.
        const TERMINAL_JOB_STATUSES = ['completed', 'failed', 'cancelled', 'interrupted'];

        function isTerminalJobStatus(status) {
            return TERMINAL_JOB_STATUSES.includes(status);
        }

        // JobsController answers a library-wide start that finds no file to
        // work on with the empty Guid and no items instead of creating a job,
        // so there is nothing to track.
        function isEmptyJobStart(data) {
            return !data.total_items || !data.job_id || data.job_id === '00000000-0000-0000-0000-000000000000';
        }
        
        // Warn user before leaving page if there's an active batch job
        // Note: We can't use async in beforeunload, so we track the active job in a variable
        let hasActiveJob = false;
        let currentJobId = null;  // Track current job ID for cancellation
        let currentJobTitle = null;  // Track current job title for progress updates
        // Pending timers that finish a tracked job's progress modal once the job
        // ends (handleJobUpdatedEvent): they are cleared when another job takes
        // over the modal, and do nothing once their job is no longer the current one.
        let jobFinalizeTimer = null;
        let progressModalCloseTimer = null;
        // Set while a job is being started: from beginJobStart() (or
        // showProgressModal()) until the job is tracked (trackJobStatus) or its
        // start is given up (endJobStart / closeProgressModal). Only jobs.js
        // changes it; other scripts go through the jobProgress API below.
        let jobStarting = false;
        // Bumped by every start, so a stale endJobStart() cannot release a newer one.
        let jobStartGeneration = 0;
        window.addEventListener('beforeunload', function(event) {
            // Clean up SSE connection
            cleanupEventSource();
            
            if (hasActiveJob) {
                // Show warning to prevent accidental navigation during batch processing
                const message = 'A batch processing job is still running. If you leave, you can resume it when you return, but progress tracking will be interrupted.';
                event.preventDefault();
                event.returnValue = message; // For older browsers
                return message;
            }
        });

        // The progress modal, its header indicator and its Cancel button follow
        // one job at a time. Starting another job while one is being started or
        // tracked would take them over (or close them if the start failed),
        // leaving the running job with no progress, no Cancel and no refresh
        // when it ends, so every action that starts a tracked job checks this first.
        // hasActiveJob stays set until the tracked job's end has been handled.
        function canStartJob() {
            if (!hasActiveJob && !jobStarting) return true;
            showMessage('Another job is already in progress (starting, running or finishing). Wait for it to finish, or cancel it, before starting a new one.', 'warning');
            return false;
        }

        // Blocks other job starts (canStartJob) while the server is asked to
        // start a job. Returns a token for endJobStart(), which releases the
        // block unless the job got tracked or another start took over since.
        function beginJobStart() {
            jobStarting = true;
            return ++jobStartGeneration;
        }

        function endJobStart(token) {
            if (token === jobStartGeneration) jobStarting = false;
        }
        
        async function processAllFiles() {
            if (!canStartJob()) return;
            if (!confirm('This will process all files in the watched directory. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will process all files in the watched directory.',
                'processed');
            
            showProgressModal('Starting processing...');
            
            try {
                console.log('[BATCH] Starting process all files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/process-all?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing all files (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No files found to process', 'info');
                    return;
                }

                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started processing ${totalItems} files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Processing All Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process all files:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processAllFilesAsync() {
            if (!canStartJob()) return;
            if (!confirm('This will process all files in the watched directory asynchronously. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will process all files in the watched directory asynchronously.',
                'processed');
            
            showProgressModal('Starting async processing...');
            
            try {
                console.log('[BATCH] Starting process all files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/process-all?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No files found to process', 'info');
                    return;
                }

                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started processing ${totalItems} files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Processing Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process all:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processSelectedFilesAsync() {
            if (selectedFiles.size === 0) {
                showMessage('Please select at least one file to process', 'error');
                return;
            }
            if (!canStartJob()) return;
            
            if (!confirm(`This will process ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''} asynchronously. Continue?`)) {
                return;
            }
            
            const forceReprocess = promptForForceReprocess(
                `This will process ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}.`,
                'processed');

            showProgressModal('Starting async processing...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log(`[BATCH] Starting process selected files request (${files.length} files)...`);
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/process-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({
                        Files: files,
                        ForceReprocess: forceReprocess
                    })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started processing ${totalItems} files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Processing Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process selected:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function clearSelectedProcessedStatus() {
            if (selectedFiles.size === 0) {
                showMessage('Please select at least one file', 'error');
                return;
            }
            const count = selectedFiles.size;
            if (!confirm(
                `Clear the renamed/normalized flags on ${count} selected file${count > 1 ? 's' : ''}?\n\n` +
                'The files will then be re-processed on the next Process / Rename / Normalize run.')) {
                return;
            }

            const files = Array.from(selectedFiles);
            try {
                const response = await fetch(apiUrl('/api/status/clear-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin',
                    body: JSON.stringify({ Files: files })
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    showMessage('Failed to clear processed status', 'error');
                    return;
                }
                const data = await response.json();
                showMessage(
                    `Cleared processed status on ${data.cleared} of ${data.requested} file(s). ` +
                    'Run Process / Rename / Normalize to reapply.',
                    'success');
                // Refresh the file list so badges update.
                if (typeof loadFiles === 'function') {
                    loadFiles(true);
                }
            } catch (err) {
                console.error('clearSelectedProcessedStatus failed', err);
                showMessage('Failed to clear processed status: ' + err.message, 'error');
            }
        }

        async function removeMetadataSelected() {
            if (selectedFiles.size === 0) {
                showMessage('Please select at least one file', 'error');
                return;
            }
            if (!canStartJob()) return;
            const count = selectedFiles.size;
            if (!confirm(
                `Remove embedded metadata (ComicInfo.xml) from ${count} selected file${count > 1 ? 's' : ''}?\n\n` +
                'Each archive will be rewritten without its ComicInfo.xml and the files will be marked unprocessed so they can be re-processed.')) {
                return;
            }

            showProgressModal('Starting metadata removal...');

            const files = Array.from(selectedFiles);
            try {
                const response = await fetch(apiUrl('/api/jobs/remove-metadata-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin',
                    body: JSON.stringify({ Files: files })
                });

                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    throw new Error('Failed to start remove metadata job');
                }

                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;

                showMessage(`Started removing metadata from ${totalItems} selected file${totalItems === 1 ? '' : 's'} in background`, 'info');
                await trackJobStatus(jobId, 'Removing Metadata from Selected Files...');
            } catch (err) {
                console.error('removeMetadataSelected failed', err);
                showMessage('Failed to remove metadata: ' + err.message, 'error');
                closeProgressModal();
            }
        }

        async function removeMetadataAll() {
            if (!canStartJob()) return;
            if (!confirm(
                'Remove embedded metadata (ComicInfo.xml) from ALL files in the library?\n\n' +
                'Every archive will be rewritten without its ComicInfo.xml and all files will be marked unprocessed so they can be re-processed. This may take a while.')) {
                return;
            }

            showProgressModal('Starting metadata removal...');

            try {
                const response = await fetch(apiUrl('/api/jobs/remove-metadata-all'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    credentials: 'same-origin'
                });

                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    throw new Error('Failed to start remove metadata job');
                }

                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;

                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No files found in library', 'info');
                    return;
                }

                showMessage(`Started removing metadata from ${totalItems} file${totalItems === 1 ? '' : 's'} in background`, 'info');
                await trackJobStatus(jobId, 'Removing Metadata from All Files...');
            } catch (err) {
                console.error('removeMetadataAll failed', err);
                showMessage('Failed to remove metadata: ' + err.message, 'error');
                closeProgressModal();
            }
        }

        async function pollJobStatusOnce(jobId) {
            // Poll job status once to catch up after SSE reconnection or to handle stuck jobs
            console.log(`[JOB ${jobId}] Polling job status once...`);
            
            try {
                const response = await fetch(apiUrl(`/api/jobs/${jobId}`), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    console.warn(`[JOB ${jobId}] Could not fetch job status: ${response.status}`);
                    return;
                }
                
                const status = await response.json();
                // Take the counts from the job's counters, as the job_updated
                // events do: processed_items counts the files that succeeded and
                // failed_items those that failed. `results` lists every file of
                // the job, including the ones not processed yet, so counting its
                // non-successes would report unfinished files as failures.
                const successCount = status.processed_items || 0;
                const errorCount = status.failed_items || 0;
                const processed = successCount + errorCount;
                const total = status.total_items || 0;
                setProgressCurrentFile(status.current_file);
                
                console.log(`[JOB ${jobId}] Current status: ${status.status}, ${processed}/${total} (${successCount} success, ${errorCount} errors)`);
                
                // Update progress UI
                updateProgress(processed, total, successCount, errorCount);
                
                // Populate progress details with already-processed files when resuming
                if (status.results && Array.isArray(status.results)) {
                    for (const result of status.results) {
                        // Only show files that have been processed (either success or error)
                        if (result.success || result.error) {
                            const filename = result.file.split(/[/\\]/).pop(); // Extract just the filename
                            addProgressDetail(filename, result.success, result.error);
                        }
                    }
                }
                
                // Handle completion states
                if (isTerminalJobStatus(status.status)) {
                    console.log(`[JOB ${jobId}] Job is ${status.status}, triggering completion handler`);
                    
                    // Simulate an SSE event to trigger completion logic
                    handleJobUpdatedEvent({
                        job_id: jobId,
                        status: status.status,
                        progress: {
                            processed: processed,
                            total: total,
                            success: successCount,
                            errors: errorCount,
                            percentage: (processed / total * 100) || 0
                        }
                    });
                }
            } catch (error) {
                console.error(`[JOB ${jobId}] Error polling job status:`, error);
            }
        }
        
        async function trackJobStatus(jobId, title) {
            // Job progress updates via SSE only - purely event-based, no polling
            console.log(`[JOB ${jobId}] Tracking job status via SSE events: ${title}`);
            
            // Set active job state IMMEDIATELY to avoid race condition where
            // SSE events arrive before this completes. This ensures we don't
            // Set active job state immediately to track progress
            hasActiveJob = true;
            currentJobId = jobId;  // Track for cancellation
            currentJobTitle = title;  // Track title for progress updates
            jobStarting = false;
            jobStartGeneration++;
            // The modal now follows this job: a previous job's pending finish
            // must neither retitle nor close it.
            clearProgressModalTimers();
            
            // Fetch initial job state to display immediately
            await pollJobStatusOnce(jobId);
            
            // From this point on, all updates come from SSE events via handleJobUpdatedEvent
            // Purely event-based - no polling, no watchdog timers
            console.log(`[JOB ${jobId}] Waiting for real-time SSE updates...`);
        }
        
        async function cancelCurrentJob() {
            if (!currentJobId) {
                showMessage('No active job to cancel', 'error');
                return;
            }
            
            if (!confirm('Are you sure you want to cancel the current batch processing job?')) {
                return;
            }
            
            try {
                console.log(`[CANCEL] Cancelling job ${currentJobId}...`);
                
                const response = await fetch(apiUrl(`/api/jobs/${currentJobId}/cancel`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    const errorData = await response.json().catch(() => ({}));
                    throw new Error(errorData.error || `Failed to cancel job (HTTP ${response.status})`);
                }
                
                const result = await response.json();
                
                if (result.success) {
                    console.log(`[CANCEL] Job ${currentJobId} cancelled successfully`);
                    showMessage('Batch processing cancelled', 'warning');
                    
                    // The polling loop will detect the cancelled status and exit
                    // We don't need to do anything else here
                } else {
                    throw new Error(result.error || 'Failed to cancel job');
                }
            } catch (error) {
                console.error(`[CANCEL] Error cancelling job ${currentJobId}:`, error);
                showMessage('Failed to cancel job: ' + error.message, 'error');
            }
        }
        
        async function checkAndResumeActiveJob() {
            // Get active job from server
            const activeJob = await getActiveJobFromServer();
            
            if (!activeJob || !activeJob.job_id) {
                console.log('[JOB RESUME] No active job found on server');
                return; // No active job
            }
            
            const activeJobId = activeJob.job_id;
            const activeJobTitle = activeJob.job_title;
            
            // Validate job_id format (should be a UUID)
            const uuidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
            if (!uuidRegex.test(activeJobId)) {
                console.warn(`[JOB RESUME] Invalid job_id format: ${activeJobId} (expected UUID) - ignoring`);
                return;
            }
            
            console.log(`[JOB RESUME] Found active job ${activeJobId} on server, checking status...`);
            
            try {
                // Check if job still exists and is active
                const response = await fetch(apiUrl(`/api/jobs/${activeJobId}`), {
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                
                if (!response.ok) {
                    if (response.status === 404) {
                        // Job not found (was cleaned up or deleted)
                        console.warn(`[JOB RESUME] Job ${activeJobId} not found (404) - was cleaned up`);
                        showMessage('Previous batch processing job is no longer available', 'warning');
                        return;
                    } else if (response.status >= 500) {
                        // Server error - don't clear the job, user can refresh to try again
                        console.error(`[JOB RESUME] Server error (${response.status}) checking job ${activeJobId}`);
                        showMessage('Server error checking job status. Please refresh to try again.', 'error');
                        return;
                    } else {
                        // Other client errors
                        console.warn(`[JOB RESUME] Error ${response.status} checking job ${activeJobId}`);
                        showMessage('Previous batch processing job is no longer available', 'warning');
                        return;
                    }
                }
                
                const status = await response.json();
                console.log(`[JOB RESUME] Job ${activeJobId} status: ${status.status}, ${status.processed_items}/${status.total_items} items processed`);
                
                // Resume if job is still running or queued
                if (status.status === 'running' || status.status === 'queued') {
                    console.log(`[JOB RESUME] Resuming job ${activeJobId}`);
                    hasActiveJob = true;
                    showProgressModal(activeJobTitle || 'Resuming Job...');
                    showMessage('Resuming active job...', 'info');
                    await trackJobStatus(activeJobId, activeJobTitle || 'Processing...');
                } else if (status.status === 'completed') {
                    // Job completed while we were away - show results
                    console.log(`[JOB RESUME] Job ${activeJobId} already completed`);
                    let successCount = 0;
                    let errorCount = 0;
                    
                    if (status.results && Array.isArray(status.results)) {
                        for (const result of status.results) {
                            if (result.success) {
                                successCount++;
                            } else {
                                errorCount++;
                            }
                        }
                    }
                    
                    const total = status.total_items || 0;
                    showMessage(`Batch processing completed: ${successCount} of ${total} files processed successfully${errorCount > 0 ? `, ${errorCount} failed` : ''}`, successCount > 0 ? 'success' : 'warning');
                    
                    // Refresh file list to show updated status
                    await loadActiveLibraryView(1, true);
                } else if (status.status === 'failed') {
                    // Job failed while we were away
                    console.error(`[JOB RESUME] Job ${activeJobId} already failed`);
                    showMessage(`Batch processing failed: ${status.error || 'Unknown error'}`, 'error');
                } else if (status.status === 'cancelled') {
                    // Job was cancelled
                    console.log(`[JOB RESUME] Job ${activeJobId} was cancelled`);
                    showMessage('Batch processing was cancelled', 'warning');
                } else if (status.status === 'interrupted') {
                    // The server restarted mid-job. Report how far it got so the user can
                    // decide whether to run it again; it is not resumed automatically.
                    console.warn(`[JOB RESUME] Job ${activeJobId} was interrupted by a restart`);
                    const processed = (status.processed_items || 0) + (status.failed_items || 0);
                    const total = status.total_items || 0;
                    showMessage(
                        `Batch processing was interrupted by a server restart after ${processed} of ${total} files. Re-run it to process the rest.`,
                        'warning');
                    await loadActiveLibraryView(1, true);
                }
            } catch (error) {
                // Network error or other exception
                console.error(`[JOB RESUME] Error checking active job ${activeJobId}:`, error);
                // Don't clear the job on network errors - it might still be running
                // User can refresh to try again
                showMessage('Could not check job status. Please refresh to try again.', 'warning');
            }
        }
        
        async function renameAllFiles() {
            if (!canStartJob()) return;
            if (!confirm('This will rename all files in the watched directory based on metadata. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will rename all files in the watched directory based on metadata.',
                'renamed');
            
            showProgressModal('Starting rename...');
            
            try {
                console.log('[BATCH] Starting rename all files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/rename-all?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start renaming all files (HTTP ${response.status})`);
                    throw new Error('Failed to start renaming job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No files found to rename', 'info');
                    return;
                }

                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started renaming ${totalItems} files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Renaming All Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting rename all files:', error);
                showMessage('Failed to start renaming: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function normalizeAllFiles() {
            if (!canStartJob()) return;
            if (!confirm('This will normalize metadata for all files in the watched directory. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will normalize metadata for all files in the watched directory.',
                'normalized');
            
            showProgressModal('Starting normalize...');
            
            try {
                console.log('[BATCH] Starting normalize all files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/normalize-all?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start normalizing all files (HTTP ${response.status})`);
                    throw new Error('Failed to start normalizing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No files found to normalize', 'info');
                    return;
                }

                console.log(`[BATCH] Created job ${jobId} for ${totalItems} files`);
                showMessage(`Started normalizing ${totalItems} files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Normalizing All Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting normalize all files:', error);
                showMessage('Failed to start normalizing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processUnmarkedFiles() {
            if (!canStartJob()) return;
            if (!confirm('This will process all unmarked files in the watched directory. Continue?')) {
                return;
            }
            
            showProgressModal('Starting async processing...');
            
            try {
                console.log('[BATCH] Starting process unmarked files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/process-unmarked'), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing unmarked files (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No unmarked files found to process', 'info');
                    return;
                }

                console.log(`[BATCH] Created job ${jobId} for ${totalItems} unmarked files`);
                showMessage(`Started processing ${totalItems} unmarked files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Processing Unmarked Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process unmarked files:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function renameUnmarkedFiles() {
            if (!canStartJob()) return;
            if (!confirm('This will rename all unmarked files in the watched directory based on metadata. Continue?')) {
                return;
            }
            
            showProgressModal('Starting async renaming...');
            
            try {
                console.log('[BATCH] Starting rename unmarked files request...');
                // Start the job
                const response = await fetch(apiUrl('/api/jobs/rename-unmarked'), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start renaming unmarked files (HTTP ${response.status})`);
                    throw new Error('Failed to start renaming job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No unmarked files found to rename', 'info');
                    return;
                }

                console.log(`[BATCH] Created job ${jobId} for ${totalItems} unmarked files`);
                showMessage(`Started renaming ${totalItems} unmarked files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Renaming Unmarked Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting rename unmarked files:', error);
                showMessage('Failed to start renaming: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function normalizeUnmarkedFiles() {
            if (!canStartJob()) return;
            if (!confirm('This will normalize metadata for all unmarked files in the watched directory. Continue?')) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                'This will normalize metadata for all unmarked files in the watched directory.',
                'normalized');

            showProgressModal('Starting async normalizing...');
            
            try {
                console.log('[BATCH] Starting normalize unmarked files request...');
                // Start the job
                const response = await fetch(apiUrl(`/api/jobs/normalize-unmarked?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start normalizing unmarked files (HTTP ${response.status})`);
                    throw new Error('Failed to start normalizing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                if (isEmptyJobStart(data)) {
                    closeProgressModal();
                    showMessage('No unmarked files found to normalize', 'info');
                    return;
                }

                console.log(`[BATCH] Created job ${jobId} for ${totalItems} unmarked files`);
                showMessage(`Started normalizing ${totalItems} unmarked files in background`, 'info');
                
                // Poll for status
                await trackJobStatus(jobId, 'Normalizing Unmarked Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting normalize unmarked files:', error);
                showMessage('Failed to start normalizing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processSelectedFiles() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'error');
                return;
            }
            if (!canStartJob()) return;
            
            if (!confirm(`This will process ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}. Continue?`)) {
                return;
            }
            
            const forceReprocess = promptForForceReprocess(
                `This will process ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}.`,
                'processed');

            showProgressModal('Starting processing...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log('[BATCH] Starting process selected files request...');
                // Start the job using the jobs endpoint
                const response = await fetch(apiUrl('/api/jobs/process-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: files, ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start processing selected files (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} selected files`);
                showMessage(`Started processing ${totalItems} selected files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Processing Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting process selected files:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function renameSelectedFiles() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'error');
                return;
            }
            if (!canStartJob()) return;
            
            if (!confirm(`This will rename ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''} based on metadata. Continue?`)) {
                return;
            }
            
            const forceReprocess = promptForForceReprocess(
                `This will rename ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''} based on metadata.`,
                'renamed');

            showProgressModal('Starting rename...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log('[BATCH] Starting rename selected files request...');
                // Start the job using the jobs endpoint
                const response = await fetch(apiUrl('/api/jobs/rename-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: files, ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start renaming selected files (HTTP ${response.status})`);
                    throw new Error('Failed to start renaming job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} selected files`);
                showMessage(`Started renaming ${totalItems} selected files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Renaming Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting rename selected files:', error);
                showMessage('Failed to start renaming: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function normalizeSelectedFiles() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'error');
                return;
            }
            if (!canStartJob()) return;
            
            if (!confirm(`This will normalize metadata for ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}. Continue?`)) {
                return;
            }
            
            const forceReprocess = promptForForceReprocess(
                `This will normalize metadata for ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''}.`,
                'normalized');

            showProgressModal('Starting normalize...');
            
            const files = Array.from(selectedFiles);
            
            try {
                console.log('[BATCH] Starting normalize selected files request...');
                // Start the job using the jobs endpoint
                const response = await fetch(apiUrl('/api/jobs/normalize-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: files, ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[BATCH] Failed to start normalizing selected files (HTTP ${response.status})`);
                    throw new Error('Failed to start normalizing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                const totalItems = data.total_items;
                
                console.log(`[BATCH] Created job ${jobId} for ${totalItems} selected files`);
                showMessage(`Started normalizing ${totalItems} selected files in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Normalizing Selected Files...');
                
            } catch (error) {
                console.error('[BATCH] Error starting normalize selected files:', error);
                showMessage('Failed to start normalizing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function processSingleFile(filepath) {
            if (!canStartJob()) return;
            if (!confirm(`Process ${filepath}?`)) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                `This will process ${filepath}.`,
                'processed');
            
            showProgressModal('Starting processing...');
            
            try {
                console.log('[SINGLE FILE] Starting process file request...');
                // Start the job using the process-selected endpoint with a single file
                const response = await fetch(apiUrl('/api/jobs/process-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: [filepath], ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[SINGLE FILE] Failed to start processing file (HTTP ${response.status})`);
                    throw new Error('Failed to start processing job');
                }
                
                const data = await response.json();
                const jobId = data.job_id;
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${filepath}`);
                showMessage(`Started processing file in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Processing File...');
                
            } catch (error) {
                console.error('[SINGLE FILE] Error starting process file:', error);
                showMessage('Failed to start processing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function renameSingleFile(filepath) {
            if (!canStartJob()) return;
            if (!confirm(`Rename ${filepath} based on metadata?`)) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                `This will rename ${filepath} based on metadata.`,
                'renamed');
            
            showProgressModal('Starting rename...');
            
            try {
                console.log('[SINGLE FILE] Starting rename file request...');
                // Start the rename job
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/rename?forceReprocess=${forceReprocess}`), {
                    method: 'POST',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[SINGLE FILE] Failed to start renaming file (HTTP ${response.status})`);
                    throw new Error('Failed to start renaming job');
                }
                
                const data = await response.json();
                const jobId = data.jobId;
                
                if (!jobId) {
                    throw new Error('No job ID returned');
                }
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${filepath}`);
                showMessage(`Started renaming file in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Renaming File...');
                
            } catch (error) {
                console.error('[SINGLE FILE] Error starting rename file:', error);
                showMessage('Failed to start renaming: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function normalizeSingleFile(filepath) {
            if (!canStartJob()) return;
            if (!confirm(`Normalize metadata for ${filepath}?`)) {
                return;
            }

            const forceReprocess = promptForForceReprocess(
                `This will normalize metadata for ${filepath}.`,
                'normalized');
            
            showProgressModal('Starting normalize...');
            
            try {
                console.log('[SINGLE FILE] Starting normalize file request...');
                // Start the normalize job
                const response = await fetch(apiUrl('/api/jobs/normalize-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: [filepath], ForceReprocess: forceReprocess })
                });
                
                if (handleAuthError(response)) {
                    closeProgressModal();
                    return;
                }
                if (!response.ok) {
                    console.error(`[SINGLE FILE] Failed to start normalizing file (HTTP ${response.status})`);
                    throw new Error('Failed to start normalizing job');
                }
                
                const result = await response.json();
                const jobId = result.job_id;
                
                if (!jobId) {
                    throw new Error('No job ID returned');
                }
                
                console.log(`[SINGLE FILE] Created job ${jobId} for file: ${filepath}`);
                showMessage(`Started normalizing file in background`, 'info');
                
                // Track job status
                await trackJobStatus(jobId, 'Normalizing File...');
                
            } catch (error) {
                console.error('[SINGLE FILE] Error starting normalize file:', error);
                showMessage('Failed to start normalizing: ' + error.message, 'error');
                closeProgressModal();
            }
        }
        
        async function deleteSingleFile(filepath) {
            if (!confirm(`Are you sure you want to delete ${filepath}?\n\nThis action cannot be undone!`)) {
                return;
            }
            
            showMessage('Deleting file...', 'info');
            
            try {
                // Use RESTful endpoint: DELETE /api/files/{encodedFilePath}
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}`), {
                    method: 'DELETE',
                    headers: getAuthHeaders()
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('File deleted successfully!', 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to delete file: ' + error.message, 'error');
            }
        }

        async function removeFileMetadata(filepath) {
            if (!confirm(`Remove embedded metadata (ComicInfo.xml) from:\n${filepath}\n\nThe archive will be rewritten without its ComicInfo.xml and the file will be marked as unprocessed. Continue?`)) {
                return;
            }

            showMessage('Removing metadata...', 'info');

            try {
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/metadata`), {
                    method: 'DELETE',
                    headers: getAuthHeaders()
                });

                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }

                showMessage('Metadata removed and file marked unprocessed.', 'success');
                await loadActiveLibraryView(1, true);
            } catch (error) {
                showMessage('Failed to remove metadata: ' + error.message, 'error');
            }
        }
        
        // Refresh the appropriate view after a read-status mutation. When a
        // series detail view is open, reload just that series's issues in-place
        // so the read state (and the per-issue Mark Read/Unread toggle) updates
        // without rebuilding the whole series library or losing the detail view.
        async function refreshAfterReadStatusChange() {
            if (libraryViewMode === 'series' && currentSeriesDetailId) {
                await loadSeriesIssues(currentSeriesDetailId, true);
            } else {
                await loadActiveLibraryView(1, true);
            }
        }

        async function markFileRead(filepath) {
            try {
                showMessage('Marking file as read...', 'info');
                
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/read`), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ read: true })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('File marked as read!', 'success');
                await refreshAfterReadStatusChange();
            } catch (error) {
                showMessage('Failed to mark file as read: ' + error.message, 'error');
            }
        }
        
        async function markFileUnread(filepath) {
            try {
                showMessage('Marking file as unread...', 'info');
                
                const encodedPath = encodeFilePathForUrl(filepath);
                const response = await fetch(apiUrl(`/api/files/${encodedPath}/read`), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ read: false })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('File marked as unread!', 'success');
                await refreshAfterReadStatusChange();
            } catch (error) {
                showMessage('Failed to mark file as unread: ' + error.message, 'error');
            }
        }
        
        async function fetchAllLibraryFilePaths() {
            // Use the legacy /api/files endpoint with per_page=-1 to grab every
            // path for whole-library bulk operations.
            const url = apiUrl('/api/files?per_page=-1');
            const response = await fetch(url, { headers: getAuthHeaders() });
            if (handleAuthError(response)) return [];
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            const data = await response.json();
            return (data.files || []).map(f => f.relative_path);
        }

        async function markAllFilesRead() {
            if (!confirm('Mark EVERY file in the library as read?\n\nThis overwrites the read status of all files and cannot be undone.')) {
                return;
            }
            
            try {
                showMessage('Marking all files as read...', 'info');
                
                const allFilePaths = await fetchAllLibraryFilePaths();
                const response = await fetch(apiUrl('/api/files/read-batch'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ files: allFilePaths, read: true })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('All files marked as read!', 'success');
                await refreshAfterReadStatusChange();
            } catch (error) {
                showMessage('Failed to mark files as read: ' + error.message, 'error');
            }
        }
        
        async function markAllFilesUnread() {
            if (!confirm('Mark EVERY file in the library as unread?\n\nThis clears the read status of all files and cannot be undone.')) {
                return;
            }
            
            try {
                showMessage('Marking all files as unread...', 'info');
                
                const allFilePaths = await fetchAllLibraryFilePaths();
                const response = await fetch(apiUrl('/api/files/read-batch'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ files: allFilePaths, read: false })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage('All files marked as unread!', 'success');
                await refreshAfterReadStatusChange();
            } catch (error) {
                showMessage('Failed to mark files as unread: ' + error.message, 'error');
            }
        }
        
        async function markSelectedFilesRead() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'warning');
                return;
            }

            if (!confirm(`Mark ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''} as read?\n\nThis overwrites the current read status and cannot be undone.`)) {
                return;
            }
            
            try {
                showMessage('Marking selected files as read...', 'info');
                
                const selectedFilePaths = Array.from(selectedFiles);
                const response = await fetch(apiUrl('/api/files/read-batch'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ files: selectedFilePaths, read: true })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage(`${selectedFiles.size} file(s) marked as read!`, 'success');
                await refreshAfterReadStatusChange();
            } catch (error) {
                showMessage('Failed to mark files as read: ' + error.message, 'error');
            }
        }
        
        async function markSelectedFilesUnread() {
            if (selectedFiles.size === 0) {
                showMessage('No files selected', 'warning');
                return;
            }

            if (!confirm(`Mark ${selectedFiles.size} selected file${selectedFiles.size > 1 ? 's' : ''} as unread?\n\nThis clears the current read status and cannot be undone.`)) {
                return;
            }
            
            try {
                showMessage('Marking selected files as unread...', 'info');
                
                const selectedFilePaths = Array.from(selectedFiles);
                const response = await fetch(apiUrl('/api/files/read-batch'), {
                    method: 'POST',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ files: selectedFilePaths, read: false })
                });
                
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                
                showMessage(`${selectedFiles.size} file(s) marked as unread!`, 'success');
                await refreshAfterReadStatusChange();
            } catch (error) {
                showMessage('Failed to mark files as unread: ' + error.message, 'error');
            }
        }
        
        function refreshFiles() {
            showMessage('Refreshing file list...', 'info');
            loadActiveLibraryView(1, true);
        }
        
        // Helper: toggle the progress indicator pill, adding the
        // mobile-sticky modifier on small viewports so it pins to the bottom
        // of the screen (the in-header pill is invisible once users scroll).
        function setProgressIndicatorVisible(visible) {
            const indicator = document.getElementById('progressIndicator');
            if (!indicator) return;
            if (visible) {
                indicator.style.display = 'flex';
                if (isMobileLibraryViewport()) {
                    indicator.classList.add('progress-indicator-btn--mobile-sticky');
                } else {
                    indicator.classList.remove('progress-indicator-btn--mobile-sticky');
                }
            } else {
                indicator.style.display = 'none';
                indicator.classList.remove('progress-indicator-btn--mobile-sticky');
            }
        }

        function showProgressModal(title) {
            // The modal now belongs to a job that is only being started: no job
            // is current until trackJobStatus() names it, so the pending finish
            // of a job that ended can neither retitle nor close it, and no other
            // job can be started meanwhile (canStartJob).
            clearProgressModalTimers();
            currentJobId = null;
            beginJobStart();
            const modal = document.getElementById('progressModal');
            const indicator = document.getElementById('progressIndicator');
            document.getElementById('progressTitle').textContent = title;
            document.getElementById('progressBarFill').style.width = '0%';
            document.getElementById('progressText').textContent = '0 / 0 files';
            document.getElementById('progressPercent').textContent = '0%';
            document.getElementById('progressProcessedCount').textContent = '0';
            document.getElementById('progressSuccessCount').textContent = '0';
            document.getElementById('progressErrorCount').textContent = '0';
            document.getElementById('progressCurrentFile').textContent = 'Waiting for the job to start...';
            progressResults = [];
            progressResultLookup = new Set();
            progressResultElements = new Map();
            renderProgressDetails();
            document.getElementById('progressCloseBtn').style.display = 'none';
            document.getElementById('progressCancelBtn').style.display = 'inline-block';  // Show cancel button
            
            // Check if the modal was previously minimized
            let wasMinimized = false;
            try {
                wasMinimized = localStorage.getItem('progressModalMinimized') === 'true';
            } catch (e) {
                // localStorage may be unavailable (e.g., private browsing mode)
                console.warn('Could not access localStorage:', e);
            }
            
            if (wasMinimized) {
                // Show the minimized indicator instead of the full modal
                modal.classList.remove('active');
                setProgressIndicatorVisible(true);
                const indicatorText = document.getElementById('progressIndicatorText');
                indicatorText.textContent = title;
            } else {
                // Show the full modal
                modal.classList.add('active');
                setProgressIndicatorVisible(false);
            }
        }
        
        function updateProgress(current, total, successCount, errorCount) {
            const percent = total > 0 ? Math.round((current / total) * 100) : 0;
            document.getElementById('progressBarFill').style.width = percent + '%';
            document.getElementById('progressText').textContent = `${current} / ${total} files`;
            document.getElementById('progressPercent').textContent = percent + '%';
            document.getElementById('progressProcessedCount').textContent = current.toString();
            document.getElementById('progressSuccessCount').textContent = successCount.toString();
            document.getElementById('progressErrorCount').textContent = errorCount.toString();
            
            // Update title with success/error counts if any errors, preserving the original job title
            let baseTitle = currentJobTitle || 'Processing Files...';
            let title = baseTitle;
            if (errorCount > 0) {
                title = `${baseTitle} - ${successCount} succeeded, ${errorCount} failed`;
            }
            document.getElementById('progressTitle').textContent = title;
            
            // Update indicator if it's visible (modal is minimized)
            const indicator = document.getElementById('progressIndicator');
            if (indicator.style.display !== 'none') {
                const indicatorText = document.getElementById('progressIndicatorText');
                indicatorText.textContent = `${current} / ${total} files (${percent}%)`;
            }
        }

        // Update the progress modal label with the latest file or status message.
        function setProgressCurrentFile(filepath, prefix = 'Latest update') {
            const label = document.getElementById('progressCurrentFile');
            if (!label) {
                return;
            }

            if (!filepath) {
                label.textContent = 'Waiting for the next completed file...';
                return;
            }

            label.textContent = `${prefix}: ${extractDisplayName(filepath)}`;
        }

        // Render the per-file processing results shown inside the progress modal.
        function renderProgressDetails() {
            const details = document.getElementById('progressDetails');
            const countLabel = document.getElementById('progressResultsCount');
            countLabel.textContent = `${progressResults.length} result${progressResults.length === 1 ? '' : 's'}`;

            if (progressResults.length === 0) {
                renderHtml(details, html`<div class="progress-results-empty">Per-file processing updates will appear here as each file completes.</div>`);
                return;
            }
            
            clearChildren(details);
            progressResults.forEach(result => {
                const entry = buildProgressResultElement(result);
                details.appendChild(entry);
                progressResultElements.set(result.key, entry);
            });
        }
        
        // Both the full re-render and the incremental single-row append build
        // the same row, so the markup lives in one place.
        function buildProgressResultElement(result) {
            return htmlToElement(html`
                <div class="progress-result-item ${result.success ? 'success' : 'error'}">
                    <div class="progress-result-status" aria-hidden="true">${result.success ? '✅' : '❌'}</div>
                    <div class="progress-result-text">
                        <strong>${result.filename}</strong>
                        <span>${result.success ? 'Completed successfully' : (result.error || 'Failed')}</span>
                    </div>
                </div>
            `);
        }

        function addProgressDetail(filename, success, error = null) {
            const resultKey = filename;
            const nextResult = {
                key: resultKey,
                filename: extractDisplayName(filename),
                success,
                error
            };

            const details = document.getElementById('progressDetails');
            const emptyState = details.querySelector('.progress-results-empty');
            if (emptyState) {
                emptyState.remove();
            }

            if (progressResultLookup.has(resultKey)) {
                const existingIndex = progressResults.findIndex(item => item.key === resultKey);
                if (existingIndex >= 0) {
                    progressResults.splice(existingIndex, 1);
                }

                const existingElement = progressResultElements.get(resultKey);
                if (existingElement) {
                    existingElement.remove();
                    progressResultElements.delete(resultKey);
                }
            }

            progressResults.unshift(nextResult);
            progressResultLookup.add(resultKey);

            const entry = buildProgressResultElement(nextResult);
            details.prepend(entry);
            progressResultElements.set(resultKey, entry);

            if (progressResults.length > MAX_PROGRESS_RESULTS) {
                const removedResult = progressResults.pop();
                if (removedResult) {
                    progressResultLookup.delete(removedResult.key);
                    const removedElement = progressResultElements.get(removedResult.key);
                    if (removedElement) {
                        removedElement.remove();
                    }
                    progressResultElements.delete(removedResult.key);
                }
            }

            setProgressCurrentFile(filename, success ? 'Completed' : 'Failed');
            document.getElementById('progressResultsCount').textContent = `${progressResults.length} result${progressResults.length === 1 ? '' : 's'}`;
        }
        
        function completeProgress() {
            document.getElementById('progressCloseBtn').style.display = 'block';
            document.getElementById('progressCancelBtn').style.display = 'none';  // Hide cancel button when complete
            setProgressCurrentFile(currentJobTitle || 'Batch job', 'Finished');
            
            // Update indicator to show completion
            const indicator = document.getElementById('progressIndicator');
            if (indicator.style.display !== 'none') {
                const indicatorText = document.getElementById('progressIndicatorText');
                indicatorText.textContent = '✅ Processing Complete';
            }
        }
        
        function closeProgressModal() {
            clearTimeout(progressModalCloseTimer);
            progressModalCloseTimer = null;
            jobStarting = false;
            jobStartGeneration++;
            document.getElementById('progressModal').classList.remove('active');
            setProgressIndicatorVisible(false);
            document.getElementById('progressCancelBtn').style.display = 'none';  // Hide cancel button
            // Clear minimized state when modal is closed
            try {
                localStorage.removeItem('progressModalMinimized');
            } catch (e) {
                // localStorage may be unavailable (e.g., private browsing mode)
                console.warn('Could not clear localStorage:', e);
            }
        }

        // Close the progress modal of a job that ended after `delay` ms, unless
        // another job has taken the modal over by then.
        function scheduleProgressModalClose(jobId, delay) {
            if (currentJobId !== jobId) return;
            clearTimeout(progressModalCloseTimer);
            progressModalCloseTimer = setTimeout(() => {
                progressModalCloseTimer = null;
                if (currentJobId === jobId) closeProgressModal();
            }, delay);
        }

        // Finish the progress modal of a job that ended after `delay` ms, unless
        // another job has taken the modal over by then (clearProgressModalTimers).
        function scheduleJobFinalize(jobId, finalize, delay) {
            clearTimeout(jobFinalizeTimer);
            jobFinalizeTimer = setTimeout(async () => {
                jobFinalizeTimer = null;
                if (currentJobId === jobId) await finalize();
            }, delay);
        }

        // Drop the pending finish (and close) of a job that ended, when another
        // job takes the progress modal over.
        function clearProgressModalTimers() {
            clearTimeout(jobFinalizeTimer);
            jobFinalizeTimer = null;
            clearTimeout(progressModalCloseTimer);
            progressModalCloseTimer = null;
        }

        // The job-tracking API used by other scripts (events.js, file-actions.js,
        // series-metadata.js); the state behind it stays owned by jobs.js.
        window.jobProgress = Object.freeze({
            canStartJob,
            beginJobStart,
            endJobStart,
            scheduleJobFinalize,
            scheduleProgressModalClose
        });
        
        function minimizeProgressModal() {
            const modal = document.getElementById('progressModal');
            const indicatorText = document.getElementById('progressIndicatorText');
            
            // Hide the modal
            modal.classList.remove('active');
            
            // Show the indicator with current progress
            const percentText = document.getElementById('progressPercent').textContent;
            const progressText = document.getElementById('progressText').textContent;
            indicatorText.textContent = `${progressText} (${percentText})`;
            setProgressIndicatorVisible(true);
            
            // Save minimized state to localStorage
            try {
                localStorage.setItem('progressModalMinimized', 'true');
            } catch (e) {
                // localStorage may be unavailable (e.g., private browsing mode, quota exceeded)
                console.warn('Could not save to localStorage:', e);
            }
        }
        
        function restoreProgressModal() {
            const modal = document.getElementById('progressModal');
            
            // Show the modal
            modal.classList.add('active');
            
            // Hide the indicator
            setProgressIndicatorVisible(false);
            
            // Clear minimized state when modal is restored
            try {
                localStorage.removeItem('progressModalMinimized');
            } catch (e) {
                // localStorage may be unavailable (e.g., private browsing mode)
                console.warn('Could not clear localStorage:', e);
            }
        }
        
        // Function to delete selected files
        async function deleteSelectedFiles() {
            const selectedFilesArray = Array.from(selectedFiles);

            if (selectedFilesArray.length === 0) {
                showMessage('No files selected', 'error');
                return;
            }
            if (!canStartJob()) return;

            // Confirm deletion
            if (!confirm(`Are you sure you want to delete ${selectedFilesArray.length} file(s)? This action cannot be undone.`)) {
                return;
            }

            showProgressModal(`Deleting ${selectedFilesArray.length} file(s)...`);

            try {
                console.log(`[BULK DELETE] Starting delete-selected job for ${selectedFilesArray.length} file(s)...`);
                const response = await fetch(apiUrl('/api/jobs/delete-selected'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...getAuthHeaders()
                    },
                    body: JSON.stringify({ Files: selectedFilesArray })
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
                const totalItems = data.totalItems || data.total_items || selectedFilesArray.length;

                console.log(`[BULK DELETE] Created job ${jobId} for ${totalItems} file(s)`);
                showMessage(`Deleting ${totalItems} file(s) in background`, 'info');

                // Clear selected files now; the server is authoritative from this
                // point on and SSE events (job_updated / file_processed /
                // file_list_updated) will refresh the library in place.
                selectedFiles.clear();
                resetSeriesSelectionState();

                // Track via the standard SSE-driven progress modal.
                await trackJobStatus(jobId, `Deleting ${totalItems} file(s)...`);
            } catch (error) {
                console.error('[BULK DELETE] Error starting delete-selected job:', error);
                showMessage('Failed to start delete: ' + error.message, 'error');
                closeProgressModal();
            }
        }
