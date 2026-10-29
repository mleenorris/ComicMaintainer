// ComicMaintainer reader page (reader.html) — the comic reader.
        //
        // Classic (non-module) script sharing the page's global scope: the
        // markup's inline on* handlers call its functions, and /js/shared.js is
        // loaded before it for the shared helpers. reader.html works offline,
        // so sw.js precaches this file (PAGE_SCRIPTS).

        let currentPage = 1;
        let totalPages = 1;
        let comicFilePath = '';
        let currentComicName = '';
        let token = '';
        let readingMode = 'manga'; // 'manga' or 'webcomic'
        // Tracks which (comic, page) pairs have been requested/loaded in webcomic
        // mode. Keyed by `${filePath}#${pageNum}` so pages from different comics
        // that share the same page number never collide when several comics are
        // stitched together in the continuous scroll container.
        let loadedPages = new Set();
        // Per-comic metadata for the continuous scroll container so prefetching
        // can resolve page bounds / titles for whichever comic an image belongs
        // to (filePath -> { totalPages, name }).
        let comicMeta = new Map();

        // Build the key used to track loaded pages for a specific comic.
        function pageLoadedKey(filePath, pageNum) {
            return `${filePath}#${pageNum}`;
        }

        // Insert a page image into the continuous container keeping it ordered
        // within its own comic (so pages from other stitched comics are not
        // disturbed and async arrivals land in the right spot).
        function insertWebcomicImage(container, img, filePath, pageNum) {
            const images = Array.from(container.querySelectorAll('img[data-page]'));
            let insertBefore = null;
            let lastSameComic = null;
            for (const existing of images) {
                if (existing.dataset.comic !== filePath) continue;
                const p = parseInt(existing.dataset.page);
                if (!isNaN(p) && p > pageNum) {
                    insertBefore = existing;
                    break;
                }
                lastSameComic = existing;
            }
            if (insertBefore) {
                container.insertBefore(img, insertBefore);
            } else if (lastSameComic) {
                container.insertBefore(img, lastSameComic.nextSibling);
            } else {
                container.appendChild(img);
            }
        }
        let isLoadingNext = false;
        let nextComicInfo = null;
        // The comic currently sitting at the end of the stitched webcomic
        // stream. New issues are appended after this one, which is not
        // necessarily the issue on screen: stitching deliberately runs ahead of
        // the reader so the transition needs no work when it is reached.
        let streamTailPath = '';
        // Comics already appended to the stream, in stitch order. Used both to
        // avoid appending an issue twice and to tell whether the reader has
        // moved forwards past an earlier issue.
        const stitchedComicPaths = new Set();
        // True when the last adjacent-issue lookup failed (network error or a
        // non-OK response) rather than legitimately reporting "no next issue".
        // Distinguishing the two lets the reader retry instead of silently
        // stalling at the bottom of the stream.
        let nextComicPrefetchFailed = false;
        const NEXT_COMIC_RETRY_MESSAGE = 'Could not load the next issue. Scroll again to retry.';
        let hasScrolledToBottom = false;
        // File paths already marked read in this reader session, tracked per
        // comic rather than as a single flag. In webcomic mode several issues
        // are stitched into one continuous scroll, so a single flag either
        // suppressed the mark for later issues or attributed it to the wrong
        // file after a transition.
        const markedReadPaths = new Set();
        // Marks currently in flight (per file path) so repeated scroll events
        // don't fire duplicate requests before the first one resolves.
        const markReadInFlight = new Set();
        let pageObserver = null; // Intersection Observer for tracking visible pages
        let currentVisiblePage = 1; // Track the currently visible page in webcomic mode
        let bufferPages = 5; // Number of pages to prefetch ahead/behind
        let allImagesLoaded = false; // Whether all prefetched images have loaded
        let isScrolling = false; // Whether a scrollTo() operation is in progress
        let prevScrollPosition = 0; // Track scroll position to determine direction
        let scrollDirection = 'forward'; // 'forward' or 'backward'
        let readingDirection = 'ltr'; // 'ltr' or 'rtl'
        let chromeHidden = false;
        let chromeIdleTimer = null;
        const CHROME_IDLE_MS = 3000;
        let chromeHintShown = false;
        // Cache of preloaded manga-mode page Blob URLs keyed by page number.
        // Lets us flip pages instantly instead of waiting on the network.
        const mangaPageCache = new Map();
        const MANGA_PRELOAD_AHEAD = 2;
        const MANGA_PRELOAD_BEHIND = 1;
        // Tracks the next-comic file whose leading pages we've already warmed
        // in the browser HTTP cache so we don't refetch them every time the
        // last page is (re)rendered. Reset whenever a fresh next-issue pointer
        // is prefetched.
        let preloadedNextComicPath = null;
        // How many leading pages of the next issue to warm so that advancing
        // to it (a full reader reload in manga mode, or an inline stitch in
        // webcomic mode) can paint the opening pages without a cold fetch.
        // Sized to cover the first page the reloaded reader shows plus the
        // neighbours it immediately preloads (manga: page 1 + MANGA_PRELOAD_AHEAD)
        // and the webcomic buffer window, so the reader isn't left waiting on a
        // network round-trip right after the transition.
        const NEXT_COMIC_PRELOAD_PAGES = 5;
        // Start warming the next issue this many pages before the final page in
        // manga mode, giving the fetches time to land in the HTTP cache before
        // the reader reloads onto the next comic. Warming only populates the
        // browser cache keyed by explicit page URL, so it can never reorder or
        // duplicate pages.
        const NEXT_COMIC_PRELOAD_LEAD_PAGES = 3;
        // How close to the end of the stitched stream (in viewport heights) the
        // reader must get before the following issue is fetched and appended.
        // Appending ahead of the reader is what makes the comic-to-comic
        // transition seamless: the pages are already in the DOM — and usually
        // decoded — by the time the reader scrolls onto them, so there is no
        // pause, no spinner and no scroll repositioning at the boundary.
        const WEBCOMIC_STITCH_AHEAD_VIEWPORTS = 2;
        // Floor for the stitch-ahead distance so short viewports still get a
        // useful head start.
        const WEBCOMIC_STITCH_AHEAD_MIN_PX = 2000;
        // Monotonic token for manga-mode page loads. Each loadPage() call claims
        // the latest token; when its (possibly out-of-order) fetch resolves it
        // only touches the DOM if it is still the most recent request. This
        // prevents a slow fetch for an earlier page from overwriting a newer
        // page that the user already advanced to (which made the same page
        // appear twice in a row and skipped the page in between).
        let mangaLoadToken = 0;

        // Get parameters from URL
        function getQueryParam(param) {
            const urlParams = new URLSearchParams(window.location.search);
            return urlParams.get(param);
        }

        // Credentials + bearer token unless signed in via Authelia; see
        // buildCredentialedFetchOptions() in /js/shared.js.
        function buildFetchOptions(extra) {
            return buildCredentialedFetchOptions(extra, token);
        }

        // Initialize reader
        async function initReader() {
            comicFilePath = getQueryParam('file');
            token = localStorage.getItem('jwt_token') || '';
            const isAutheliaAuth = isAutheliaAuthenticated();

            if (!comicFilePath) {
                showError('No comic file specified');
                return;
            }

            // Check authentication - allow either JWT token or Authelia authentication
            if (!token && !isAutheliaAuth) {
                window.location.href = '/login.html';
                return;
            }

            // Restore saved reading direction (per-user, all comics).
            try {
                const savedDir = localStorage.getItem('readingDirection');
                if (savedDir === 'rtl' || savedDir === 'ltr') readingDirection = savedDir;
            } catch (_e) { /* localStorage may be unavailable */ }

            applyReaderEmailVisibility();

            try {
                // Get comic info
                const response = await fetch(
                    `/api/ComicReader/info?filePath=${encodeURIComponent(comicFilePath)}`,
                    buildFetchOptions()
                );

                if (!response.ok) {
                    if (response.status === 401) {
                        window.location.href = '/login.html';
                        return;
                    }
                    throw new Error('Failed to load comic info');
                }

                const data = await response.json();
                totalPages = data.pageCount;
                currentComicName = data.fileName;

                document.getElementById('comicTitle').textContent = data.fileName;
                document.title = `${data.fileName} - Comic Reader`;

                // Populate page slider range
                const slider = document.getElementById('pageSlider');
                if (slider) {
                    slider.max = String(totalPages);
                    slider.min = '1';
                    slider.value = '1';
                }

                // Load saved reading mode preference
                readingMode = localStorage.getItem('readingMode') || 'manga';
                updateModeUI();
                updateDirectionUI();

                // Check if URL hash indicates we should go to last page
                const urlHash = window.location.hash;
                let initialPage;

                if (urlHash === '#lastpage') {
                    // Navigate to last page (for previous comic navigation)
                    initialPage = totalPages;
                    // Clear the hash from URL
                    history.replaceState(null, '', window.location.pathname + window.location.search);
                } else {
                    // Load saved reading progress
                    const savedPage = await loadReadingProgress();
                    initialPage = Math.min(savedPage, totalPages); // Ensure page is within bounds
                }

                currentPage = initialPage;

                // Load first page or all pages depending on mode
                if (readingMode === 'webcomic') {
                    await loadWebcomicMode(currentPage);
                } else {
                    await loadPage(currentPage);
                }

                // Prefetch next comic info for seamless transition
                await prefetchNextComic();

                // If we opened near the final page (e.g. a short issue, or
                // restored progress close to the end), warm the next issue now
                // that its pointer is available. Manga mode only; webcomic mode
                // stitches the next issue inline instead.
                if (readingMode === 'webcomic') {
                    // Append the following issue ahead of the reader (no-op
                    // until the end of this one is actually in reach) so the
                    // transition costs nothing when it is reached.
                    void ensureNextComicStitched();
                } else if (currentPage >= totalPages - (NEXT_COMIC_PRELOAD_LEAD_PAGES - 1)) {
                    preloadNextComicPages();
                }

                // Wire up interactive UI helpers
                setupTapZones();
                setupChromeAutoHide();
            } catch (error) {
                console.error('Error initializing reader:', error);
                showError('Failed to load comic: ' + error.message);
            }
        }

        // Toggle between manga and webcomic reading modes
        async function toggleReadingMode() {
            // Store the current page before switching modes
            const pageBeforeSwitch = currentPage;

            readingMode = readingMode === 'manga' ? 'webcomic' : 'manga';
            localStorage.setItem('readingMode', readingMode);
            updateModeUI();

            // Reload content in new mode
            const content = document.getElementById('readerContent');
            content.innerHTML = '<div class="loading-state"><div class="reader-spinner" aria-hidden="true"></div><div>Switching mode…</div></div>'
                + tapZonesHtml();

            // Reset state for mode switch
            isScrolling = true;
            allImagesLoaded = false;
            prevScrollPosition = 0;
            // Clear manga page cache when switching out of manga mode so we
            // don't hold blob URLs for pages we won't reuse.
            clearMangaPageCache();

            if (readingMode === 'webcomic') {
                await loadWebcomicMode(pageBeforeSwitch);
            } else {
                // Leaving the continuous stream: drop the stitched-stream
                // bookkeeping so next-issue lookups resolve against the comic
                // actually being read.
                streamTailPath = '';
                stitchedComicPaths.clear();
                await loadPage(pageBeforeSwitch);
                // The pointer may have been resolved against the stream tail
                // rather than the comic on screen; re-resolve it.
                await prefetchNextComic();
            }
        }

        function tapZonesHtml() {
            return '<div class="tap-zones" id="tapZones" aria-hidden="true">'
                + '<div class="tap-zone tap-zone--left" id="tapZoneLeft"></div>'
                + '<div class="tap-zone tap-zone--middle" id="tapZoneMiddle"></div>'
                + '<div class="tap-zone tap-zone--right" id="tapZoneRight"></div>'
                + '</div>';
        }

        // Update UI based on reading mode
        function updateModeUI() {
            const modeLabel = document.getElementById('modeToggleLabel');
            const content = document.getElementById('readerContent');
            const footer = document.getElementById('readerFooter');
            const pageInfo = document.getElementById('pageInfo');
            const container = document.getElementById('readerContainer');

            if (readingMode === 'webcomic') {
                if (modeLabel) modeLabel.textContent = 'Webcomic';
                content.classList.add('webcomic-mode');
                container.classList.remove('reader-mode-manga');
                container.classList.add('reader-mode-webcomic');
                // The footer only contains the manga slider / nav; hide it
                // entirely in webcomic mode to avoid an empty bar.
                footer.style.display = 'none';
                if (pageInfo) pageInfo.style.display = 'inline';
            } else {
                if (modeLabel) modeLabel.textContent = 'Manga';
                content.classList.remove('webcomic-mode');
                container.classList.remove('reader-mode-webcomic');
                container.classList.add('reader-mode-manga');
                footer.style.display = 'flex';
                if (pageInfo) pageInfo.style.display = 'inline';
            }
        }

        function updateDirectionUI() {
            const lbl = document.getElementById('directionToggleLabel');
            if (lbl) lbl.textContent = readingDirection === 'rtl' ? 'RTL' : 'LTR';
            const btn = document.getElementById('directionToggleBtn');
            if (btn) btn.title = readingDirection === 'rtl' ? 'Reading right-to-left (R)' : 'Reading left-to-right (R)';
        }

        function toggleReadingDirection() {
            readingDirection = readingDirection === 'ltr' ? 'rtl' : 'ltr';
            try { localStorage.setItem('readingDirection', readingDirection); } catch (_e) { /* noop */ }
            updateDirectionUI();
            showMessage(`Reading direction: ${readingDirection.toUpperCase()}`, 'info');
        }

        // Load pages in continuous scroll mode with buffering (Kavita-style)
        async function loadWebcomicMode(startPage = 1) {
            const content = document.getElementById('readerContent');
            content.innerHTML = '<div class="webcomic-container" id="webcomicContainer"></div>';
            
            const container = document.getElementById('webcomicContainer');
            loadedPages.clear();
            comicMeta.clear();
            // Register the comic we are starting with so prefetching knows its
            // page bounds.
            comicMeta.set(comicFilePath, { totalPages: totalPages, name: currentComicName });
            // The stream starts (and, for now, ends) with this comic.
            streamTailPath = comicFilePath;
            stitchedComicPaths.clear();
            stitchedComicPaths.add(comicFilePath);
            allImagesLoaded = false;
            
            // Reset current visible page
            currentVisiblePage = startPage;
            currentPage = startPage;
            
            // Setup page observer for tracking visible pages
            setupPageObserver();
            
            // Calculate initial prefetch range based on buffer
            const [startIdx, endIdx] = calculatePrefetchIndices(startPage);
            
            console.log('Initial prefetch: pages', startIdx, 'to', endIdx);
            
            // Load buffered pages instead of all pages
            for (let i = startIdx; i <= endIdx; i++) {
                await loadWebcomicPage(i, container);
            }
            
            // Wait for initial images to load, then scroll to starting page
            await waitForImagesToLoad();
            
            // Scroll to the specified starting page
            if (startPage > startIdx) {
                const targetImage = document.querySelector(`img[data-page="${startPage}"]`);
                if (targetImage) {
                    setTimeout(() => {
                        targetImage.scrollIntoView({ behavior: 'auto', block: 'start' });
                        isScrolling = false;
                    }, 100);
                } else {
                    isScrolling = false;
                }
            } else {
                isScrolling = false;
            }
            
            // Set up scroll listener for loading next comic
            setupWebcomicScrollListener();

            // A short issue can fit entirely on screen (or reading can resume on
            // the final page), in which case no scroll event ever fires and the
            // issue would never be marked read. Run the completion check once
            // after the initial render.
            await maybeMarkFinishedComic();

            // Likewise, if the whole issue is already on screen the reader may
            // never scroll far enough to trigger the stitch-ahead check, so run
            // it once here too.
            void ensureNextComicStitched();
        }
        
        // Calculate prefetch indices based on current page and buffer
        function calculatePrefetchIndices(pageNum = currentPage, comicTotalPages = totalPages) {
            const startIdx = Math.max(1, pageNum - bufferPages);
            const endIdx = Math.min(comicTotalPages, pageNum + bufferPages);
            return [startIdx, endIdx];
        }
        
        // Wait for all pending images to load
        function waitForImagesToLoad() {
            return Promise.all(
                Array.from(document.querySelectorAll('img[data-page]'))
                    .filter(img => !img.complete)
                    .map(img => new Promise(resolve => {
                        img.onload = img.onerror = resolve;
                    }))
            ).then(() => {
                allImagesLoaded = true;
                console.log('All prefetched images loaded');
            });
        }

        // Load a single page in webcomic mode
        async function loadWebcomicPage(pageNum, container) {
            const loadKey = pageLoadedKey(comicFilePath, pageNum);
            if (loadedPages.has(loadKey)) return;
            // Mark as in-flight before awaiting so a concurrent call for the
            // same page can't pass the guard above and append a duplicate image.
            loadedPages.add(loadKey);
            
            try {
                const isAutheliaAuth = isAutheliaAuthenticated();
                const fetchOptions = {
                    credentials: 'include'
                };
                
                if (!isAutheliaAuth && token) {
                    fetchOptions.headers = {
                        'Authorization': `Bearer ${token}`
                    };
                }
                
                const response = await fetch(
                    `/api/ComicReader/page?filePath=${encodeURIComponent(comicFilePath)}&page=${pageNum}`,
                    fetchOptions
                );

                if (!response.ok) {
                    throw new Error('Failed to load page');
                }

                const blob = await response.blob();
                const imageUrl = URL.createObjectURL(blob);
                
                const img = document.createElement('img');
                img.src = imageUrl;
                img.alt = `Page ${pageNum}`;
                img.dataset.page = pageNum;
                img.dataset.comic = comicFilePath;
                container.appendChild(img);
                
                // Observe the image for visibility tracking
                if (pageObserver) {
                    pageObserver.observe(img);
                }
            } catch (error) {
                console.error(`Error loading page ${pageNum}:`, error);
                // Allow a retry since this page was never actually inserted.
                loadedPages.delete(loadKey);
            }
        }
        
        // Load a single page in webcomic mode (async, non-blocking)
        function loadWebcomicPageAsync(pageNum, container, filePath = comicFilePath) {
            const loadKey = pageLoadedKey(filePath, pageNum);
            if (loadedPages.has(loadKey)) return;
            
            // Mark as being loaded
            loadedPages.add(loadKey);
            
            const isAutheliaAuth = isAutheliaAuthenticated();
            const fetchOptions = {
                credentials: 'include'
            };
            
            if (!isAutheliaAuth && token) {
                fetchOptions.headers = {
                    'Authorization': `Bearer ${token}`
                };
            }
            
            fetch(
                `/api/ComicReader/page?filePath=${encodeURIComponent(filePath)}&page=${pageNum}`,
                fetchOptions
            )
            .then(response => {
                if (!response.ok) throw new Error('Failed to load page');
                return response.blob();
            })
            .then(blob => {
                const imageUrl = URL.createObjectURL(blob);
                
                const img = document.createElement('img');
                img.src = imageUrl;
                img.alt = `Page ${pageNum}`;
                img.dataset.page = pageNum;
                img.dataset.comic = filePath;
                
                // Insert in correct position within this comic's pages
                insertWebcomicImage(container, img, filePath, pageNum);
                
                // Observe the image for visibility tracking
                if (pageObserver) {
                    pageObserver.observe(img);
                }
                
                console.log('Loaded page', pageNum, 'asynchronously');
            })
            .catch(error => {
                console.error(`Error loading page ${pageNum}:`, error);
                // Remove from loaded set so we can retry
                loadedPages.delete(loadKey);
            });
        }

        // Update page info display in webcomic mode
        function updatePageInfoInWebcomicMode() {
            const pageInfo = document.getElementById('pageInfo');
            if (pageInfo && readingMode === 'webcomic') {
                pageInfo.textContent = `Page ${currentVisiblePage} / ${totalPages}`;
            }
        }
        
        // Setup scroll listener for webcomic mode with direction tracking
        function setupWebcomicScrollListener() {
            const content = document.getElementById('readerContent');
            
            let scrollTimeout;
            
            const handleScroll = () => {
                const scrollPosition = content.scrollTop;
                
                // Determine scroll direction
                if (scrollPosition > prevScrollPosition) {
                    scrollDirection = 'forward';
                } else if (scrollPosition < prevScrollPosition) {
                    scrollDirection = 'backward';
                }
                prevScrollPosition = scrollPosition;
                
                // Clear existing timeout
                if (scrollTimeout) {
                    clearTimeout(scrollTimeout);
                }
                
                // Set new timeout for scroll end detection
                scrollTimeout = setTimeout(() => {
                    handleScrollEnd();
                }, 150);
                
                // Check if should load next comic
                handleWebcomicScroll();
            };
            
            const handleScrollEnd = () => {
                // Find closest visible image when scrolling stops
                if (!isScrolling && allImagesLoaded) {
                    const images = Array.from(document.querySelectorAll('img[data-page]'));
                    const closestImage = findClosestVisibleImage(images);
                    
                    if (closestImage) {
                        const pageNum = parseInt(closestImage.dataset.page);
                        const filePath = closestImage.dataset.comic || comicFilePath;
                        if (!isNaN(pageNum) && (pageNum !== currentPage || filePath !== comicFilePath)) {
                            setActiveComic(filePath);
                            currentPage = pageNum;
                            currentVisiblePage = pageNum;
                            updatePageInfoInWebcomicMode();
                            saveReadingProgress(pageNum);
                        }
                    }
                }
            };
            
            content.removeEventListener('scroll', handleScroll);
            content.addEventListener('scroll', handleScroll);
        }
        
        // Find the closest visible image to the top of the viewport
        function findClosestVisibleImage(images) {
            let closestImage = null;
            let closestDistance = Number.MAX_VALUE;
            
            for (const image of images) {
                const rect = image.getBoundingClientRect();
                const distanceToTop = Math.abs(rect.top);
                
                // Check if image is visible and closer to top
                if (rect.bottom >= 0 && rect.top <= window.innerHeight) {
                    if (distanceToTop < closestDistance) {
                        closestDistance = distanceToTop;
                        closestImage = image;
                    }
                }
            }
            
            return closestImage;
        }

        // Setup IntersectionObserver to track which page is currently visible in webcomic mode
        function setupPageObserver() {
            // Clean up existing observer
            if (pageObserver) {
                pageObserver.disconnect();
            }
            
            const container = document.getElementById('webcomicContainer');
            if (!container) return;
            
            // Create observer to track which page is most visible and trigger prefetching
            pageObserver = new IntersectionObserver((entries) => {
                if (!allImagesLoaded || isScrolling) {
                    return; // Skip if images are still loading or scrolling
                }
                
                // Find the entry with the highest intersection ratio
                let mostVisible = null;
                let highestRatio = 0;
                
                entries.forEach(entry => {
                    if (entry.intersectionRatio > highestRatio && entry.isIntersecting) {
                        highestRatio = entry.intersectionRatio;
                        mostVisible = entry;
                    }
                    
                    // Trigger prefetch when a page becomes visible
                    if (entry.isIntersecting) {
                        const pageNum = parseInt(entry.target.dataset.page);
                        const filePath = entry.target.dataset.comic || comicFilePath;
                        if (!isNaN(pageNum)) {
                            prefetchPages(pageNum, filePath);
                        }
                    }
                });
                
                // Update currentPage and currentVisiblePage based on the most visible image
                if (mostVisible && mostVisible.isIntersecting) {
                    const pageNum = parseInt(mostVisible.target.dataset.page);
                    const filePath = mostVisible.target.dataset.comic || comicFilePath;
                    if (!isNaN(pageNum)) {
                        // The visible page may belong to a different stitched comic
                        // than the one currently considered "active"; sync the
                        // active comic context so progress is saved to the right
                        // file and the title/bounds stay correct.
                        setActiveComic(filePath);
                        if (pageNum !== currentPage) {
                            currentPage = pageNum;
                            // Save progress as the user scrolls
                            saveReadingProgress(pageNum);
                        }
                        if (pageNum !== currentVisiblePage) {
                            currentVisiblePage = pageNum;
                            updatePageInfoInWebcomicMode();
                        }
                    }
                }
            }, {
                root: document.getElementById('readerContent'),
                threshold: [0, 0.25, 0.5, 0.75, 1.0] // Multiple thresholds for better tracking
            });
            
            // Observe all page images
            const images = container.querySelectorAll('img[data-page]');
            images.forEach(img => {
                pageObserver.observe(img);
            });
        }
        
        // Switch the "active" comic context (used while scrolling through a
        // continuous stream of stitched comics) so progress saving, the title
        // bar and page bounds follow whichever comic is currently on screen.
        function setActiveComic(filePath) {
            if (!filePath || filePath === comicFilePath) return;
            const meta = comicMeta.get(filePath);
            if (!meta) return;
            // Moving forward onto a later issue of the stream means the
            // previous one has been read through. Because the next issue is now
            // stitched in before the reader gets to the end, that issue never
            // sits at the bottom of the stream, so the bottom-of-stream check
            // alone would never mark it read.
            if (isStitchedBefore(comicFilePath, filePath)) {
                void markFileAsRead(comicFilePath);
            }
            comicFilePath = filePath;
            currentComicName = meta.name;
            totalPages = meta.totalPages;
            const titleEl = document.getElementById('comicTitle');
            if (titleEl) titleEl.textContent = meta.name;
        }

        // True when `earlierPath` was stitched into the stream before
        // `laterPath`, i.e. the reader moving from the former to the latter is
        // moving forwards through the series.
        function isStitchedBefore(earlierPath, laterPath) {
            if (!earlierPath || !laterPath || earlierPath === laterPath) return false;
            const order = Array.from(stitchedComicPaths);
            const earlierIdx = order.indexOf(earlierPath);
            const laterIdx = order.indexOf(laterPath);
            return earlierIdx >= 0 && laterIdx >= 0 && earlierIdx < laterIdx;
        }

        // Prefetch pages based on current page and scroll direction
        function prefetchPages(visiblePage, filePath = comicFilePath) {
            const meta = comicMeta.get(filePath);
            const comicTotalPages = meta ? meta.totalPages : totalPages;
            const [startIdx, endIdx] = calculatePrefetchIndices(visiblePage, comicTotalPages);
            
            console.log('Prefetching pages', startIdx, 'to', endIdx, 'from visible page', visiblePage);
            
            const container = document.getElementById('webcomicContainer');
            if (!container) return;
            
            for (let i = startIdx; i <= endIdx; i++) {
                if (!loadedPages.has(pageLoadedKey(filePath, i))) {
                    loadWebcomicPageAsync(i, container, filePath);
                }
            }
        }

        // Handle scroll in webcomic mode
        async function handleWebcomicScroll() {
            const content = document.getElementById('readerContent');
            const scrollPosition = content.scrollTop + content.clientHeight;
            const scrollHeight = content.scrollHeight;
            const distanceToEnd = scrollHeight - scrollPosition;

            // Append the following issue while the reader is still a screen or
            // two away from the end of the stream. By the time the reader gets
            // there the pages are already in the DOM, so the comic-to-comic
            // boundary is just more scrolling — no fetch, no spinner, no jump.
            const stitchAhead = Math.max(
                content.clientHeight * WEBCOMIC_STITCH_AHEAD_VIEWPORTS,
                WEBCOMIC_STITCH_AHEAD_MIN_PX);
            if (distanceToEnd < stitchAhead) {
                void ensureNextComicStitched();
            }

            // Check if scrolled near bottom (within 200px)
            if (distanceToEnd < 200) {
                // Reaching the bottom means the issue whose pages sit at the end
                // of the stitched stream has been read through. Mark that issue
                // read — even when there is NO adjacent next issue (i.e. the last
                // issue of a series), otherwise the final issue never gets its
                // read flag set, so the whole series stays pinned to the Continue
                // Reading row and reappears on refresh.
                await maybeMarkFinishedComic();

                // The stitch-ahead above normally has the next issue in place
                // already; this is the catch-up path for a reader that arrived
                // at the bottom first (a fast flick, or a failed/at-the-time
                // unresolved lookup).
                if (!isLoadingNext) {
                    // A stitch that happens now means there is more to read, so
                    // no end-of-stream notice — even though the pointer it
                    // leaves behind describes the issue *after* the one just
                    // appended.
                    const stitched = await ensureNextComicStitched();

                    const tailPath = streamTailPath || comicFilePath;
                    const tailFullyRequested = tailPath && isComicFullyRequested(tailPath);
                    if (!stitched && (!tailFullyRequested || !nextComicInfo || !nextComicInfo.hasAdjacent)) {
                        showStreamEndMessage(!tailFullyRequested || nextComicPrefetchFailed
                            ? NEXT_COMIC_RETRY_MESSAGE
                            : 'End of series');
                    }
                }
            }
        }

        // True once every page of the given comic has been requested, i.e. the
        // stream really does end with that issue's final page. Stitching the
        // next issue before then would append it above pages that are still
        // filling in.
        function isComicFullyRequested(filePath) {
            const meta = comicMeta.get(filePath);
            if (!meta || !meta.totalPages) return false;
            return loadedPages.has(pageLoadedKey(filePath, meta.totalPages));
        }

        // Append the next issue to the end of the continuous stream. Idempotent
        // and safe to call repeatedly from scroll handlers: it returns early
        // unless the stream genuinely ends with a fully requested issue that has
        // an adjacent next issue which is not stitched in yet.
        // Returns true when an issue was appended.
        async function ensureNextComicStitched() {
            if (readingMode !== 'webcomic') return false;
            if (isLoadingNext) return false;

            isLoadingNext = true;
            try {
                const tailPath = streamTailPath || comicFilePath;
                if (!tailPath) return false;
                if (!isComicFullyRequested(tailPath)) {
                    await requestTailFinalPage(tailPath);
                    if (!isComicFullyRequested(tailPath)) return false;
                }

                if (!nextComicInfo) {
                    // No pointer available: the lookup either failed or has not
                    // completed yet. Resolve it here so a transient error — or a
                    // race with the initial prefetch — doesn't strand the reader
                    // at the bottom with no way forward and no explanation.
                    await prefetchNextComic(tailPath);
                }

                if (!nextComicInfo || !nextComicInfo.hasAdjacent || !nextComicInfo.filePath) return false;
                // A stale pointer that names an issue already in the stream
                // would otherwise duplicate it; re-resolve from the tail.
                if (stitchedComicPaths.has(nextComicInfo.filePath)) {
                    await prefetchNextComic(tailPath);
                    if (!nextComicInfo || !nextComicInfo.hasAdjacent || !nextComicInfo.filePath) return false;
                    if (stitchedComicPaths.has(nextComicInfo.filePath)) return false;
                }

                return await stitchNextComic(tailPath, nextComicInfo.filePath);
            } catch (error) {
                console.error('Error stitching next comic:', error);
                showStreamEndMessage(NEXT_COMIC_RETRY_MESSAGE);
                return false;
            } finally {
                isLoadingNext = false;
            }
        }

        async function requestTailFinalPage(tailPath) {
            const meta = comicMeta.get(tailPath);
            const container = document.getElementById('webcomicContainer');
            if (!meta || !meta.totalPages || !container) return;
            await loadWebcomicPageForNextComic(meta.totalPages, container, tailPath);
        }

        // Append the given issue after the current stream tail: a transition
        // separator, a start marker, and the opening buffer of pages. Nothing
        // here touches the viewport — the content goes in below whatever the
        // reader is looking at, which is exactly what makes the transition
        // seamless.
        // Returns true when the issue was appended.
        async function stitchNextComic(tailPath, nextPath) {
            const container = document.getElementById('webcomicContainer');
            if (!container || !nextPath || nextPath === tailPath) return false;

            const response = await fetch(
                `/api/ComicReader/info?filePath=${encodeURIComponent(nextPath)}`,
                buildFetchOptions()
            );

            if (!response.ok) {
                // Leave the stream as it is and surface a retry hint; a later
                // scroll to the bottom tries again.
                showStreamEndMessage(NEXT_COMIC_RETRY_MESSAGE);
                return false;
            }

            const data = await response.json();

            const tailMeta = comicMeta.get(tailPath);
            const leavingComicName = tailMeta ? tailMeta.name : currentComicName;

            const transitionDiv = document.createElement('div');
            transitionDiv.className = 'comic-transition';
            transitionDiv.innerHTML = `
                <div class="comic-transition-title">◆ ◆ ◆</div>
                <div class="comic-transition-info">
                    <div class="comic-transition-leaving">Finished: ${escapeHtml(leavingComicName)}</div>
                    <div class="comic-transition-arrow">↓</div>
                    <div class="comic-transition-entering">Continue: ${escapeHtml(data.fileName)}</div>
                </div>
                <div class="comic-transition-subtitle">Keep scrolling</div>
            `;
            container.appendChild(transitionDiv);

            // Marker for the start of the new comic. The id is unique per comic
            // so markers from earlier transitions are never re-selected.
            const startMarker = document.createElement('div');
            startMarker.id = `newComicStartMarker-${encodeURIComponent(nextPath)}`;
            startMarker.className = 'new-comic-start-marker';
            startMarker.dataset.comicPath = nextPath;
            container.appendChild(startMarker);

            // Register the new comic so prefetching, the title bar and progress
            // saving can resolve its bounds once its pages scroll into view.
            comicMeta.set(nextPath, { totalPages: data.pageCount, name: data.fileName });
            stitchedComicPaths.add(nextPath);
            streamTailPath = nextPath;

            // Load the opening buffer in parallel so a slow page doesn't
            // serially block the ones after it.
            const [startIdx, endIdx] = calculatePrefetchIndices(1, data.pageCount);
            const pageLoads = [];
            for (let i = startIdx; i <= endIdx; i++) {
                pageLoads.push(loadWebcomicPageForNextComic(i, container, nextPath));
            }
            const loadedImages = await Promise.all(pageLoads);
            if (loadedImages.some(img => !img)) {
                const stitchedImages = Array.from(container.querySelectorAll('img[data-page]'))
                    .filter(img => img.dataset.comic === nextPath);
                for (const img of stitchedImages) {
                    if (pageObserver) pageObserver.unobserve(img);
                    img.remove();
                }
                for (let i = startIdx; i <= endIdx; i++) {
                    loadedPages.delete(pageLoadedKey(nextPath, i));
                }
                transitionDiv.remove();
                startMarker.remove();
                comicMeta.delete(nextPath);
                stitchedComicPaths.delete(nextPath);
                streamTailPath = tailPath;
                showStreamEndMessage(NEXT_COMIC_RETRY_MESSAGE);
                return false;
            }

            // A previous end-of-series / retry notice no longer applies once
            // another issue has successfully been stitched in.
            clearStreamEndMessage();

            // Resolve the pointer for the issue that follows the new tail, so
            // the next stitch can start as soon as the reader gets close.
            await prefetchNextComic(nextPath);
            return true;
        }

        // Marks the issue at the end of the stitched stream as read when the
        // reader has reached its final page.
        async function maybeMarkFinishedComic() {
            const finishedPath = getFinishedComicAtBottom();
            if (!finishedPath) return;
            hasScrolledToBottom = true;
            await markFileAsRead(finishedPath);
        }

        // Returns the file path of the comic that the reader has just finished,
        // based on the last page image in the continuous container. Returns null
        // while the tail of that comic is still loading (or while the next issue
        // is being stitched in) so a partially rendered issue is never marked
        // read before the reader has actually reached its final page.
        function getFinishedComicAtBottom() {
            if (isLoadingNext) return null;

            const content = document.getElementById('readerContent');
            const container = document.getElementById('webcomicContainer');
            if (!content || !container) return null;

            // The reader must actually be at the end of the rendered stream; a
            // short issue whose pages are all loaded is not "finished" until the
            // reader has scrolled to the bottom of it.
            if (content.scrollHeight - (content.scrollTop + content.clientHeight) >= 200) {
                return null;
            }

            const images = container.querySelectorAll('img[data-page]');
            const last = images[images.length - 1];
            if (!last) return null;

            const filePath = last.dataset.comic || comicFilePath;
            if (!filePath) return null;

            const meta = comicMeta.get(filePath);
            const pageNum = parseInt(last.dataset.page);
            if (meta && meta.totalPages > 0 && !isNaN(pageNum) && pageNum < meta.totalPages) {
                return null; // Final page of this issue is not rendered yet.
            }

            return filePath;
        }

        // Prefetch next comic information.
        //
        // In webcomic mode the pointer must be resolved from the comic at the
        // end of the stitched stream (which runs ahead of the comic on screen),
        // not from the active comic, or the reader would keep re-stitching the
        // issue it already appended.
        async function prefetchNextComic(filePath = streamTailPath || comicFilePath) {
            try {
                const isAutheliaAuth = isAutheliaAuthenticated();
                const fetchOptions = {
                    credentials: 'include'
                };
                
                if (!isAutheliaAuth && token) {
                    fetchOptions.headers = {
                        'Authorization': `Bearer ${token}`
                    };
                }
                
                const response = await fetch(
                    `/api/ComicReader/adjacent?filePath=${encodeURIComponent(filePath)}&direction=next`,
                    fetchOptions
                );

                if (response.ok) {
                    nextComicInfo = await response.json();
                    nextComicPrefetchFailed = false;
                } else {
                    // Drop any previous pointer: keeping a stale one risks
                    // stitching in an issue that is no longer adjacent to the
                    // comic currently being read.
                    nextComicInfo = null;
                    nextComicPrefetchFailed = true;
                }
            } catch (error) {
                console.error('Error prefetching next comic:', error);
                nextComicInfo = null;
                nextComicPrefetchFailed = true;
            }
        }

        // Shows a single status line at the end of the stitched stream, either
        // the end-of-series notice or a retry hint after a failed lookup. Reuses
        // one element so repeated scrolls can't stack duplicate messages; an
        // existing message is only re-worded in place, never re-appended.
        function showStreamEndMessage(text) {
            const container = document.getElementById('webcomicContainer');
            if (!container) return;
            const existing = document.getElementById('webcomicStreamEndMessage');
            if (existing) {
                existing.textContent = text;
                return;
            }
            const message = document.createElement('div');
            message.id = 'webcomicStreamEndMessage';
            message.className = 'loading-indicator';
            message.textContent = text;
            container.appendChild(message);
        }

        function clearStreamEndMessage() {
            const message = document.getElementById('webcomicStreamEndMessage');
            if (message) message.remove();
        }

        // Load a page for the next comic (needed to maintain correct page numbering)
        async function loadWebcomicPageForNextComic(pageNum, container, filePath) {
            const loadKey = pageLoadedKey(filePath, pageNum);
            if (loadedPages.has(loadKey)) return;
            // Reserve the key before awaiting so a concurrent loader (e.g. the
            // IntersectionObserver-triggered prefetch that fires as soon as the
            // first page appears) can't slip past the guard above and append a
            // duplicate image for the same page while this fetch is in flight.
            loadedPages.add(loadKey);
            
            try {
                const isAutheliaAuth = isAutheliaAuthenticated();
                const fetchOptions = {
                    credentials: 'include'
                };
                
                if (!isAutheliaAuth && token) {
                    fetchOptions.headers = {
                        'Authorization': `Bearer ${token}`
                    };
                }
                
                const response = await fetch(
                    `/api/ComicReader/page?filePath=${encodeURIComponent(filePath)}&page=${pageNum}`,
                    fetchOptions
                );

                if (!response.ok) {
                    throw new Error('Failed to load page');
                }

                const blob = await response.blob();
                const imageUrl = URL.createObjectURL(blob);
                
                const img = document.createElement('img');
                img.src = imageUrl;
                img.alt = `Page ${pageNum}`;
                img.dataset.page = pageNum; // Local page number within the new comic
                img.dataset.comic = filePath; // Track which comic this page belongs to
                // Insert in the correct position within this comic's pages so
                // parallel (out-of-order) arrivals land in ascending page order
                // rather than in fetch-completion order.
                insertWebcomicImage(container, img, filePath, pageNum);
                
                // Observe the image for visibility tracking
                if (pageObserver) {
                    pageObserver.observe(img);
                }
                
                return img;
            } catch (error) {
                console.error(`Error loading page ${pageNum}:`, error);
                // Release the reservation so the page can be retried later.
                loadedPages.delete(loadKey);
                return null;
            }
        }
        
        // Reverse the meaning of prev/next when reading right-to-left so the
        // arrows / tap-zones / footer buttons feel natural for manga.
        function effectivePrev() {
            return readingDirection === 'rtl' ? _navNext() : _navPrev();
        }
        function effectiveNext() {
            return readingDirection === 'rtl' ? _navPrev() : _navNext();
        }

        // Internal "go one page back / forward" helpers shared by prev/next
        // buttons, keyboard shortcuts, and tap zones.
        async function _navPrev() {
            if (currentPage > 1) {
                loadPage(currentPage - 1);
            } else if (readingMode === 'manga') {
                await navigateToPreviousComic();
            }
        }
        async function _navNext() {
            if (currentPage < totalPages) {
                loadPage(currentPage + 1);
            } else if (readingMode === 'manga') {
                await navigateToNextComic();
            }
        }

        // Fetch a manga page Blob, caching the resulting Blob URL so subsequent
        // visits / preloads are instant. Returns the blob URL on success or
        // null on failure (the caller handles UI / error messages).
        async function fetchMangaPageUrl(pageNum) {
            if (pageNum < 1 || pageNum > totalPages) return null;
            if (mangaPageCache.has(pageNum)) return mangaPageCache.get(pageNum);
            try {
                const response = await fetch(
                    `/api/ComicReader/page?filePath=${encodeURIComponent(comicFilePath)}&page=${pageNum}`,
                    buildFetchOptions()
                );
                if (!response.ok) {
                    if (response.status === 401) {
                        window.location.href = '/login.html';
                    }
                    return null;
                }
                const blob = await response.blob();
                const url = URL.createObjectURL(blob);
                mangaPageCache.set(pageNum, url);
                return url;
            } catch (e) {
                console.error('fetchMangaPageUrl failed for page', pageNum, e);
                return null;
            }
        }

        function clearMangaPageCache() {
            for (const url of mangaPageCache.values()) {
                try { URL.revokeObjectURL(url); } catch (_e) { /* noop */ }
            }
            mangaPageCache.clear();
        }

        // Trim the cache so we only keep ~6 nearby pages around `centerPage`.
        function trimMangaPageCache(centerPage) {
            const keep = MANGA_PRELOAD_AHEAD + MANGA_PRELOAD_BEHIND + 1;
            if (mangaPageCache.size <= keep + 4) return; // small slack
            for (const key of Array.from(mangaPageCache.keys())) {
                if (Math.abs(key - centerPage) > MANGA_PRELOAD_AHEAD + MANGA_PRELOAD_BEHIND + 1) {
                    try { URL.revokeObjectURL(mangaPageCache.get(key)); } catch (_e) { /* noop */ }
                    mangaPageCache.delete(key);
                }
            }
        }

        function preloadAdjacentMangaPages(pageNum) {
            const targets = [];
            for (let i = 1; i <= MANGA_PRELOAD_AHEAD; i++) targets.push(pageNum + i);
            for (let i = 1; i <= MANGA_PRELOAD_BEHIND; i++) targets.push(pageNum - i);
            for (const p of targets) {
                if (p >= 1 && p <= totalPages && !mangaPageCache.has(p)) {
                    // Fire-and-forget; errors are logged inside fetchMangaPageUrl.
                    fetchMangaPageUrl(p);
                }
            }
        }

        // Warm the browser HTTP cache with the first page(s) of the next issue
        // so that advancing to it (which triggers a full reader reload) can
        // render page 1 instantly instead of waiting on a cold network fetch.
        // Uses fetch (not new Image()) so the JWT Authorization header is sent
        // and the cached response is reusable by the reloaded reader, which
        // re-requests the identical page URL. The /page endpoint responds with
        // `Cache-Control: public, max-age=3600`, so draining the body here is
        // enough to store it for the subsequent same-URL request.
        async function preloadNextComicPages() {
            if (!nextComicInfo || !nextComicInfo.hasAdjacent || !nextComicInfo.filePath) return;
            const nextPath = nextComicInfo.filePath;
            // Ignore a stale pointer that still references the current comic.
            if (nextPath === comicFilePath) return;
            if (preloadedNextComicPath === nextPath) return; // already warmed
            preloadedNextComicPath = nextPath;
            for (let p = 1; p <= NEXT_COMIC_PRELOAD_PAGES; p++) {
                try {
                    const response = await fetch(
                        `/api/ComicReader/page?filePath=${encodeURIComponent(nextPath)}&page=${p}`,
                        buildFetchOptions()
                    );
                    if (!response.ok) break; // out of pages or an error; stop early
                    // Drain the body so the response is fully received and stored.
                    await response.blob();
                } catch (e) {
                    console.error('Error preloading next comic page', p, e);
                    break;
                }
            }
        }

        // Load a specific page (manga mode)
        async function loadPage(pageNum) {
            if (pageNum < 1 || pageNum > totalPages) {
                return;
            }

            // Claim the latest load token so out-of-order fetch completions for
            // an earlier page don't clobber a newer page the user moved on to.
            const loadToken = ++mangaLoadToken;

            currentPage = pageNum;
            updateUI();

            const content = document.getElementById('readerContent');
            // Only show the spinner if we'll need to actually fetch — cached
            // pages should appear instantly.
            if (!mangaPageCache.has(pageNum)) {
                content.innerHTML = '<div class="loading-state"><div class="reader-spinner" aria-hidden="true"></div><div>Loading page…</div></div>'
                    + tapZonesHtml();
                setupTapZones();
            }

            try {
                const imageUrl = await fetchMangaPageUrl(pageNum);
                // A newer loadPage() superseded us while awaiting the fetch;
                // abort so we don't render a stale page over the current one.
                if (loadToken !== mangaLoadToken) {
                    return;
                }
                if (!imageUrl) {
                    throw new Error('Failed to load page');
                }

                content.innerHTML = `<img src="${imageUrl}" class="page-image page-fade" alt="Page ${pageNum}" decoding="async" fetchpriority="high">`
                    + tapZonesHtml();
                setupTapZones();
                // Fade in once the browser has painted the new src.
                requestAnimationFrame(() => {
                    const img = content.querySelector('.page-image');
                    if (img) img.classList.remove('page-fade');
                });

                // Save reading progress
                saveReadingProgress(pageNum);

                // Mark as read when reaching last page in manga mode
                if (totalPages > 0 && pageNum >= totalPages) {
                    await markFileAsRead(comicFilePath);
                }

                // Prefetch neighbours so the next page change is instant.
                preloadAdjacentMangaPages(pageNum);
                trimMangaPageCache(pageNum);

                // As the reader nears the end of the issue, warm the next one so
                // advancing to it is as seamless as possible. Starting a few
                // pages early gives the fetches time to land in the cache before
                // the reload. Fire-and-forget; a no-op until the next-issue
                // pointer has been prefetched, and guarded so it only warms once.
                if (pageNum >= totalPages - (NEXT_COMIC_PRELOAD_LEAD_PAGES - 1)) {
                    preloadNextComicPages();
                }
            } catch (error) {
                console.error('Error loading page:', error);
                showError('Failed to load page: ' + error.message);
            }
        }

        // Update UI elements
        function updateUI() {
            const pageInfoText = `Page ${currentPage} / ${totalPages}`;
            const pageInfo = document.getElementById('pageInfo');
            if (pageInfo) pageInfo.textContent = pageInfoText;
            const footerPageInfo = document.getElementById('footerPageInfo');
            if (footerPageInfo) footerPageInfo.textContent = `${currentPage} / ${totalPages}`;
            const slider = document.getElementById('pageSlider');
            if (slider) slider.value = String(currentPage);
            const progressBar = document.getElementById('readerProgressBar');
            if (progressBar && totalPages > 0) {
                const pct = Math.min(100, Math.max(0, (currentPage / totalPages) * 100));
                progressBar.style.width = pct + '%';
            }

            // In manga mode, keep navigation buttons enabled at boundaries to allow comic navigation
            // In webcomic mode, buttons are hidden anyway by updateModeUI()
            if (readingMode === 'manga') {
                // Buttons are always enabled in manga mode to allow navigation between comics
                document.getElementById('prevBtn').disabled = false;
                document.getElementById('nextBtn').disabled = false;
            } else {
                // In other modes (if any), use traditional boundary checking
                document.getElementById('prevBtn').disabled = currentPage <= 1;
                document.getElementById('nextBtn').disabled = currentPage >= totalPages;
            }
        }

        // Navigation functions (wired to footer Prev/Next buttons in the
        // visible reading direction).
        async function previousPage() { return effectivePrev(); }
        async function nextPage() { return effectiveNext(); }

        // Slider handlers — `input` is fired while dragging (live page-info
        // update), `change` is fired on release and triggers the actual load.
        function handleSliderInput(event) {
            const v = parseInt(event.target.value, 10);
            if (!isNaN(v)) {
                const footerPageInfo = document.getElementById('footerPageInfo');
                if (footerPageInfo) footerPageInfo.textContent = `${v} / ${totalPages}`;
            }
        }
        function handleSliderChange(event) {
            const v = parseInt(event.target.value, 10);
            if (!isNaN(v)) {
                if (readingMode === 'manga') {
                    loadPage(v);
                } else {
                    const img = document.querySelector(`img[data-page="${v}"]`);
                    if (img) img.scrollIntoView({ behavior: 'smooth', block: 'start' });
                }
            }
        }

        // Navigate to the next comic in the list. Uses location.replace so
        // history.back() from anywhere inside a multi-comic reading session
        // still returns to the page the user came from originally.
        async function navigateToNextComic() {
            try {
                // Reuse the already-prefetched next-issue pointer when we have
                // one so pressing Next at the last page doesn't pay for an
                // extra round-trip before the reload. Fall back to a fresh
                // lookup if prefetch failed or hasn't completed yet.
                let nextInfo = nextComicInfo;
                if (!nextInfo || !nextInfo.hasAdjacent || !nextInfo.filePath
                    || nextInfo.filePath === comicFilePath) {
                    const response = await fetch(
                        `/api/ComicReader/adjacent?filePath=${encodeURIComponent(comicFilePath)}&direction=next`,
                        buildFetchOptions()
                    );
                    if (!response.ok) throw new Error('Failed to fetch next comic info');
                    nextInfo = await response.json();
                }
                if (nextInfo.hasAdjacent && nextInfo.filePath) {
                    window.location.replace(`/reader.html?file=${encodeURIComponent(nextInfo.filePath)}`);
                } else {
                    showMessage('No more issues in this series', 'info');
                }
            } catch (error) {
                console.error('Error navigating to next comic:', error);
                showMessage('Failed to load next comic', 'error');
            }
        }

        // Navigate to the previous comic in the list. Uses location.replace
        // so a single Back press still leaves the reader.
        async function navigateToPreviousComic() {
            try {
                const response = await fetch(
                    `/api/ComicReader/adjacent?filePath=${encodeURIComponent(comicFilePath)}&direction=prev`,
                    buildFetchOptions()
                );
                if (!response.ok) throw new Error('Failed to fetch previous comic info');
                const prevInfo = await response.json();
                if (prevInfo.hasAdjacent && prevInfo.filePath) {
                    window.location.replace(`/reader.html?file=${encodeURIComponent(prevInfo.filePath)}#lastpage`);
                } else {
                    showMessage('This is the first issue in this series', 'info');
                }
            } catch (error) {
                console.error('Error navigating to previous comic:', error);
                showMessage('Failed to load previous comic', 'error');
            }
        }

        function goToPage(pageNum) {
            loadPage(parseInt(pageNum));
        }

        // Exit the reader. Strategy:
        //   1. Prefer back/forward navigation when we have a stored snapshot
        //      AND a same-origin referrer pointing at the home page — browsers
        //      restore native scroll position and any "bfcache" instantly.
        //   2. Otherwise navigate to the saved origin URL from sessionStorage
        //      so the caller page can rehydrate from its snapshot.
        //   3. Fallback: navigate to /.
        function goBack() {
            let snapshot = null;
            try {
                const raw = sessionStorage.getItem('comicReaderReturnState');
                if (raw) snapshot = JSON.parse(raw);
            } catch (_e) { /* ignore */ }

            const sameOriginReferrer = (() => {
                try {
                    if (!document.referrer) return null;
                    const ref = new URL(document.referrer);
                    return ref.origin === window.location.origin ? ref : null;
                } catch (_e) { return null; }
            })();

            // If the referrer is the home page on the same origin, history.back()
            // restores native scroll/state better than any URL navigation.
            if (sameOriginReferrer
                && (sameOriginReferrer.pathname === '/' || sameOriginReferrer.pathname.endsWith('/index.html'))) {
                window.history.back();
                return;
            }

            if (snapshot && snapshot.originUrl) {
                try {
                    const origin = new URL(snapshot.originUrl, window.location.origin);
                    if (origin.origin === window.location.origin) {
                        window.location.href = origin.toString();
                        return;
                    }
                } catch (_e) { /* fall through */ }
            }

            window.location.href = '/';
        }

        // Fullscreen functionality
        function toggleFullscreen() {
            const container = document.getElementById('readerContainer');
            const icon = document.getElementById('fullscreenIcon');

            if (!document.fullscreenElement) {
                container.requestFullscreen().then(() => {
                    container.classList.add('fullscreen-mode');
                    if (icon) icon.textContent = '⮽';
                    resetChromeIdleTimer();
                }).catch(err => {
                    console.error('Error entering fullscreen:', err);
                });
            } else {
                document.exitFullscreen().then(() => {
                    container.classList.remove('fullscreen-mode');
                    if (icon) icon.textContent = '⛶';
                });
            }
        }

        // Listen for fullscreen changes
        document.addEventListener('fullscreenchange', () => {
            const container = document.getElementById('readerContainer');
            const icon = document.getElementById('fullscreenIcon');
            if (!document.fullscreenElement) {
                container.classList.remove('fullscreen-mode');
                if (icon) icon.textContent = '⛶';
            }
        });

        // Help dialog (with focus trap + body-scroll lock)
        let helpPreviousFocus = null;
        function showHelp() {
            const dlg = document.getElementById('shortcutsHelp');
            const backdrop = document.getElementById('shortcutsHelpBackdrop');
            helpPreviousFocus = document.activeElement;
            dlg.classList.add('show');
            backdrop.classList.add('show');
            dlg.focus();
        }
        function hideHelp() {
            document.getElementById('shortcutsHelp').classList.remove('show');
            document.getElementById('shortcutsHelpBackdrop').classList.remove('show');
            if (helpPreviousFocus && typeof helpPreviousFocus.focus === 'function') {
                helpPreviousFocus.focus();
                helpPreviousFocus = null;
            }
        }

        // Go-to-page inline prompt (triggered by the 'G' shortcut)
        function showGotoPrompt() {
            const dlg = document.getElementById('gotoPrompt');
            const input = document.getElementById('gotoPromptInput');
            if (!dlg || !input) return;
            input.max = String(totalPages);
            input.value = String(currentPage);
            dlg.classList.add('show');
            setTimeout(() => { input.focus(); input.select(); }, 0);
        }
        function hideGotoPrompt() {
            const dlg = document.getElementById('gotoPrompt');
            if (dlg) dlg.classList.remove('show');
        }
        function submitGotoPage() {
            const input = document.getElementById('gotoPromptInput');
            if (!input) return;
            const v = parseInt(input.value, 10);
            hideGotoPrompt();
            if (!isNaN(v) && v >= 1 && v <= totalPages) {
                if (readingMode === 'manga') {
                    loadPage(v);
                } else {
                    const img = document.querySelector(`img[data-page="${v}"]`);
                    if (img) img.scrollIntoView({ behavior: 'smooth', block: 'start' });
                }
            }
        }

        // Email this comic to a saved ereader device. Self-contained here because
        // reader.html does not load the library's main.js.
        async function applyReaderEmailVisibility() {
            const button = document.getElementById('emailComicBtn');
            if (!button) return;
            // Sending is a write operation; hide the button for read-only users
            // rather than letting them hit a 403.
            try {
                const response = await fetch('/api/auth/user', buildFetchOptions());
                if (!response.ok) return;
                const user = await response.json();
                button.hidden = user.canModifyLibrary === false;
            } catch (_) { /* leave the button visible; the API still enforces */ }
        }

        async function openReaderEmailPrompt() {
            const dlg = document.getElementById('emailPrompt');
            const select = document.getElementById('emailPromptDevice');
            const sendBtn = document.getElementById('emailPromptSendBtn');
            if (!dlg || !select || !sendBtn) return;
            if (!comicFilePath) {
                showMessage('No comic is open.', 'error');
                return;
            }

            select.replaceChildren(new Option('Loading devices…', ''));
            sendBtn.disabled = true;
            dlg.classList.add('show');

            try {
                const [status, devices] = await Promise.all([
                    readerEmailJson('/api/email/status').catch(() => null),
                    readerEmailJson('/api/email/devices')
                ]);

                const list = Array.isArray(devices.devices) ? devices.devices : [];
                if (!list.length) {
                    select.replaceChildren(new Option('No devices saved', ''));
                    showMessage('Add an ereader device from the library menu first.', 'error');
                    return;
                }

                select.replaceChildren(...list.map(d => new Option(`${d.name} (${d.emailAddress})`, d.id)));

                if (status && status.configured === false) {
                    showMessage('Email delivery is not configured. Ask an administrator to set the SMTP settings.', 'error');
                    return;
                }

                sendBtn.disabled = false;
            } catch (error) {
                select.replaceChildren(new Option('Failed to load devices', ''));
                showMessage('Failed to load ereader devices: ' + error.message, 'error');
            }
        }

        function hideReaderEmailPrompt() {
            document.getElementById('emailPrompt')?.classList.remove('show');
        }

        async function readerEmailJson(path, options = {}) {
            const response = await fetch(path, buildFetchOptions({
                ...options,
                headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
            }));
            if (!response.ok) {
                let message = `HTTP error! status: ${response.status}`;
                try {
                    const body = await response.json();
                    if (body && (body.error || body.message)) message = body.error || body.message;
                } catch (_) {}
                throw new Error(message);
            }
            if (response.status === 204) return {};
            try {
                return await response.json();
            } catch (_) {
                return {};
            }
        }

        async function submitReaderEmail() {
            const deviceId = document.getElementById('emailPromptDevice')?.value;
            const format = document.getElementById('emailPromptFormat')?.value;
            const sendBtn = document.getElementById('emailPromptSendBtn');
            if (!deviceId) {
                showMessage('Choose an ereader device.', 'error');
                return;
            }
            if (sendBtn) sendBtn.disabled = true;
            try {
                const result = await readerEmailJson('/api/email/send', {
                    method: 'POST',
                    body: JSON.stringify({
                        files: [comicFilePath],
                        deviceId: parseInt(deviceId, 10),
                        deliveryFormat: format === 'device' ? undefined : format,
                        skipAlreadyDelivered: false
                    })
                });
                hideReaderEmailPrompt();
                const queued = Array.isArray(result.queued) ? result.queued.length : 0;
                showMessage(queued ? 'Comic queued for delivery.' : 'Nothing was queued.', queued ? 'success' : 'info');
            } catch (error) {
                showMessage('Failed to email comic: ' + error.message, 'error');
            } finally {
                if (sendBtn) sendBtn.disabled = false;
            }
        }

        // Show a friendlier error state with a "Back to library" button.
        function showError(message) {
            const content = document.getElementById('readerContent');
            const safeMsg = escapeHtml(String(message || 'Something went wrong.'));
            content.innerHTML =
                `<div class="error">
                    <div class="error-icon" aria-hidden="true">⚠️</div>
                    <div class="error-title">Couldn't load this comic</div>
                    <div class="error-message">${safeMsg}</div>
                    <button class="reader-btn reader-btn--primary" onclick="goBack()">← Back to library</button>
                </div>`;
        }

        // Show toast notification
        function showMessage(message, type = 'info') {
            const container = document.getElementById('toastContainer');
            const toast = document.createElement('div');
            toast.className = `toast ${type}`;
            toast.textContent = message;

            container.appendChild(toast);

            // Trigger animation
            setTimeout(() => {
                toast.classList.add('show');
            }, 10);

            // Remove after 3 seconds
            setTimeout(() => {
                toast.classList.remove('show');
                setTimeout(() => {
                    toast.remove();
                }, 300);
            }, 3000);
        }

        // Tap zones: left = prev, right = next (or reversed in RTL), middle = toggle chrome.
        function setupTapZones() {
            const left = document.getElementById('tapZoneLeft');
            const middle = document.getElementById('tapZoneMiddle');
            const right = document.getElementById('tapZoneRight');
            if (left && !left._wired) {
                left.addEventListener('click', () => { if (readingMode === 'manga') effectivePrev(); });
                left._wired = true;
            }
            if (right && !right._wired) {
                right.addEventListener('click', () => { if (readingMode === 'manga') effectiveNext(); });
                right._wired = true;
            }
            if (middle && !middle._wired) {
                middle.addEventListener('click', () => { toggleChromeHidden(); });
                middle._wired = true;
            }
        }

        // Auto-hide chrome after a few seconds of inactivity. Any mouse move,
        // tap, or key press resets the timer and re-shows the chrome.
        function setupChromeAutoHide() {
            const container = document.getElementById('readerContainer');
            const reset = () => resetChromeIdleTimer();
            ['mousemove', 'touchstart', 'pointerdown', 'keydown'].forEach(ev => {
                document.addEventListener(ev, reset, { passive: true });
            });
            container.addEventListener('focusin', reset);
            resetChromeIdleTimer();
        }

        function resetChromeIdleTimer() {
            if (chromeHidden) {
                setChromeHidden(false);
            }
            if (chromeIdleTimer) clearTimeout(chromeIdleTimer);
            chromeIdleTimer = setTimeout(() => {
                // Only auto-hide if no modal is open and the user isn't
                // interacting with a form control.
                const help = document.getElementById('shortcutsHelp');
                const goto = document.getElementById('gotoPrompt');
                const emailPrompt = document.getElementById('emailPrompt');
                if (help && help.classList.contains('show')) return;
                if (goto && goto.classList.contains('show')) return;
                if (emailPrompt && emailPrompt.classList.contains('show')) return;
                setChromeHidden(true);
            }, CHROME_IDLE_MS);
        }

        function setChromeHidden(hidden) {
            chromeHidden = hidden;
            const container = document.getElementById('readerContainer');
            container.classList.toggle('chrome-hidden', hidden);
            // Show the hint exactly once per session when chrome first hides.
            if (hidden && !chromeHintShown) {
                chromeHintShown = true;
                const hint = document.getElementById('chromeHint');
                if (hint) {
                    setTimeout(() => { hint.classList.remove('show-once'); }, 2500);
                }
            }
        }

        function toggleChromeHidden() {
            if (chromeIdleTimer) clearTimeout(chromeIdleTimer);
            setChromeHidden(!chromeHidden);
            // If we just showed it, start the idle countdown again.
            if (!chromeHidden) resetChromeIdleTimer();
        }

        // Keyboard shortcuts
        document.addEventListener('keydown', (e) => {
            // Don't handle shortcuts if help is shown
            if (document.getElementById('shortcutsHelp').classList.contains('show')) {
                if (e.key === 'Escape') {
                    hideHelp();
                }
                return;
            }
            // Don't intercept keys while typing in the goto prompt.
            const goto = document.getElementById('gotoPrompt');
            if (goto && goto.classList.contains('show')) {
                if (e.key === 'Enter') { e.preventDefault(); submitGotoPage(); }
                else if (e.key === 'Escape') { e.preventDefault(); hideGotoPrompt(); }
                return;
            }
            // Escape closes the email prompt rather than exiting the reader.
            const emailPrompt = document.getElementById('emailPrompt');
            if (emailPrompt && emailPrompt.classList.contains('show')) {
                if (e.key === 'Escape') { e.preventDefault(); hideReaderEmailPrompt(); }
                return;
            }
            // Ignore shortcuts when focus is in any other input/textarea.
            const tag = (document.activeElement && document.activeElement.tagName) || '';
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') {
                return;
            }

            switch (e.key) {
                case 'ArrowLeft':
                case 'a':
                case 'A':
                    if (readingMode === 'manga') {
                        e.preventDefault();
                        effectivePrev();
                    }
                    break;
                case 'ArrowRight':
                case 'd':
                case 'D':
                    if (readingMode === 'manga') {
                        e.preventDefault();
                        effectiveNext();
                    }
                    break;
                case ' ': // Space
                    if (readingMode === 'manga') {
                        e.preventDefault();
                        if (e.shiftKey) _navPrev(); else _navNext();
                    }
                    break;
                case 'Home':
                    if (readingMode === 'manga') {
                        e.preventDefault();
                        loadPage(1);
                    } else {
                        document.getElementById('readerContent').scrollTop = 0;
                    }
                    break;
                case 'End':
                    if (readingMode === 'manga') {
                        e.preventDefault();
                        loadPage(totalPages);
                    } else {
                        const content = document.getElementById('readerContent');
                        content.scrollTop = content.scrollHeight;
                    }
                    break;
                case 'g':
                case 'G':
                    e.preventDefault();
                    showGotoPrompt();
                    break;
                case 'm':
                case 'M':
                    e.preventDefault();
                    toggleReadingMode();
                    break;
                case 'r':
                case 'R':
                    e.preventDefault();
                    toggleReadingDirection();
                    break;
                case 'f':
                case 'F':
                    e.preventDefault();
                    toggleFullscreen();
                    break;
                case '?':
                    e.preventDefault();
                    showHelp();
                    break;
                case 'Escape':
                    if (document.fullscreenElement) {
                        document.exitFullscreen();
                    } else {
                        goBack();
                    }
                    break;
            }
        });

        // Mark a specific comic file as read. The caller captures the path so a
        // mark can never be attributed to a different comic that became active
        // while the request was in flight.
        async function markFileAsRead(filePath = comicFilePath) {
            if (!filePath) return;
            if (markedReadPaths.has(filePath) || markReadInFlight.has(filePath)) return;

            markReadInFlight.add(filePath);
            try {
                const isAutheliaAuth = isAutheliaAuthenticated();
                const fetchOptions = {
                    method: 'POST',
                    credentials: 'include'
                };
                
                if (!isAutheliaAuth && token) {
                    fetchOptions.headers = {
                        'Authorization': `Bearer ${token}`
                    };
                }
                
                const response = await fetch(
                    `/api/ComicReader/mark-read?filePath=${encodeURIComponent(filePath)}`,
                    fetchOptions
                );
                
                if (response.ok) {
                    markedReadPaths.add(filePath);
                    console.log('Marked file as read:', filePath);
                } else {
                    // Leave the path unmarked so a later end-of-comic event retries.
                    console.error('Failed to mark file as read:', response.status);
                }
            } catch (error) {
                console.error('Error marking file as read:', error);
            } finally {
                markReadInFlight.delete(filePath);
            }
        }

        // ------------------------------------------------------------------
        // Reading progress (with offline queueing)
        //
        // Progress updates are plain POSTs, which simply fail while the device
        // is offline. Because comics can now be downloaded and read without a
        // connection, a failed update is parked in localStorage and replayed
        // once the network returns, so the server ends up with the page the
        // user actually reached instead of where they were when they went
        // offline.
        // ------------------------------------------------------------------
        const PENDING_PROGRESS_KEY = 'pendingReadingProgress';
        let flushingPendingProgress = false;

        function readPendingProgress() {
            try {
                const raw = localStorage.getItem(PENDING_PROGRESS_KEY);
                if (!raw) return new Map();
                const parsed = JSON.parse(raw);
                if (!parsed || typeof parsed !== 'object') return new Map();
                if (Array.isArray(parsed)) {
                    return new Map(parsed.filter(entry => Array.isArray(entry) && entry.length === 2));
                }
                return new Map(Object.entries(parsed));
            } catch (_e) {
                return new Map();
            }
        }

        function writePendingProgress(pending) {
            try {
                if (!pending || pending.size === 0) {
                    localStorage.removeItem(PENDING_PROGRESS_KEY);
                } else {
                    localStorage.setItem(PENDING_PROGRESS_KEY, JSON.stringify(Array.from(pending.entries())));
                }
            } catch (error) {
                console.log('Unable to persist pending reading progress:', error);
            }
        }

        // One entry per comic: only the furthest point the user reached while
        // offline matters, so a later update for the same file replaces the
        // earlier one rather than queueing every page turn.
        function queuePendingProgress(filePath, pageNum) {
            if (!filePath || !pageNum) return;
            const pending = readPendingProgress();
            pending.set(filePath, { page: pageNum, updatedAt: Date.now() });
            writePendingProgress(pending);
        }

        function clearPendingProgress(filePath) {
            const pending = readPendingProgress();
            if (!pending.has(filePath)) return;
            pending.delete(filePath);
            writePendingProgress(pending);
        }

        function getPendingProgress(filePath) {
            const entry = readPendingProgress().get(filePath);
            return entry && entry.page ? entry.page : null;
        }

        // Returns true when the request reached the server (so the queued entry
        // can be dropped), false when it should be retried later.
        async function postReadingProgress(filePath, pageNum) {
            const response = await fetch(
                `/api/ComicReader/progress?filePath=${encodeURIComponent(filePath)}&page=${pageNum}`,
                buildFetchOptions({ method: 'POST' })
            );

            if (response.ok) {
                return true;
            }

            // 5xx (and the service worker's synthetic 503 while offline) are
            // transient; a 4xx will never succeed on replay, so the entry is
            // dropped rather than retried forever.
            if (response.status >= 500) {
                throw new Error(`Server returned ${response.status}`);
            }

            console.error('Failed to save reading progress:', response.status);
            return true;
        }

        // Replay everything parked while offline. Runs on reconnect, on reader
        // start-up, and after each successful save so a queue left behind by an
        // earlier session is not stranded.
        async function flushPendingProgress() {
            if (flushingPendingProgress) return;
            const pending = readPendingProgress();
            const filePaths = Array.from(pending.keys());
            if (filePaths.length === 0) return;

            flushingPendingProgress = true;
            try {
                for (const filePath of filePaths) {
                    const entry = pending.get(filePath);
                    if (!entry || !entry.page) {
                        clearPendingProgress(filePath);
                        continue;
                    }
                    try {
                        await postReadingProgress(filePath, entry.page);
                    } catch (error) {
                        // Still unreachable; keep this and any remaining
                        // entries queued for the next attempt.
                        console.log('Reading progress still pending:', error);
                        return;
                    }
                    // Only drop the entry if a newer page was not queued while
                    // this request was in flight.
                    const current = readPendingProgress().get(filePath);
                    if (current && current.updatedAt === entry.updatedAt) {
                        clearPendingProgress(filePath);
                    }
                }
            } finally {
                flushingPendingProgress = false;
            }
        }

        // Save reading progress
        async function saveReadingProgress(pageNum) {
            try {
                await postReadingProgress(comicFilePath, pageNum);
                console.log('Saved reading progress:', comicFilePath, 'page', pageNum);
                clearPendingProgress(comicFilePath);
                await flushPendingProgress();
            } catch (error) {
                console.log('Queueing reading progress until back online:', error);
                queuePendingProgress(comicFilePath, pageNum);
            }
        }

        // Load reading progress. A page queued offline is newer than whatever
        // the server currently holds, so it wins until it has been replayed.
        async function loadReadingProgress() {
            const pendingPage = getPendingProgress(comicFilePath);

            try {
                const response = await fetch(
                    `/api/ComicReader/progress?filePath=${encodeURIComponent(comicFilePath)}`,
                    buildFetchOptions()
                );

                if (response.ok) {
                    const data = await response.json();
                    console.log('Loaded reading progress:', comicFilePath, 'page', data.currentPage);
                    return pendingPage || data.currentPage;
                }

                console.error('Failed to load reading progress:', response.status);
                return pendingPage || 1; // Default to page 1
            } catch (error) {
                console.error('Error loading reading progress:', error);
                return pendingPage || 1; // Default to page 1
            }
        }

        // ------------------------------------------------------------------
        // Offline reading
        //
        // The service worker keeps a dedicated cache of downloaded comics and
        // serves their pages ahead of the network, so a saved comic reads the
        // same way with or without a connection. This UI drives that cache:
        // it asks the worker to download every page of the current comic,
        // reports progress, and can delete the download again.
        // ------------------------------------------------------------------
        let offlineDownloadInProgress = false;
        let offlineComicSaved = false;

        function offlineButtonElements() {
            return {
                button: document.getElementById('offlineBtn'),
                label: document.getElementById('offlineBtnLabel')
            };
        }

        function updateOfflineButton(text, title) {
            const { button, label } = offlineButtonElements();
            if (!button || !label) return;
            button.hidden = false;
            label.textContent = text;
            button.title = title;
            button.setAttribute('aria-label', title);
            button.disabled = offlineDownloadInProgress;
        }

        function sendServiceWorkerMessage(message) {
            return new Promise((resolve) => {
                const controller = navigator.serviceWorker && navigator.serviceWorker.controller;
                if (!controller) {
                    resolve(null);
                    return;
                }
                const channel = new MessageChannel();
                const timeout = setTimeout(() => resolve(null), 5000);
                channel.port1.onmessage = (event) => {
                    clearTimeout(timeout);
                    resolve(event.data);
                };
                controller.postMessage(message, [channel.port2]);
            });
        }

        async function refreshOfflineStatus() {
            if (!('serviceWorker' in navigator) || !navigator.serviceWorker.controller) {
                return;
            }
            const response = await sendServiceWorkerMessage({ type: 'LIST_OFFLINE_COMICS' });
            const comics = (response && response.comics) || [];
            offlineComicSaved = comics.some((comic) => comic.filePath === comicFilePath);
            if (offlineDownloadInProgress) return;
            updateOfflineButton(
                offlineComicSaved ? 'Saved' : 'Save offline',
                offlineComicSaved
                    ? 'Remove this comic from offline storage'
                    : 'Save this comic for offline reading'
            );
        }

        async function toggleOfflineDownload() {
            if (offlineDownloadInProgress) return;

            const controller = navigator.serviceWorker && navigator.serviceWorker.controller;
            if (!controller) {
                showMessage('Offline downloads are not available in this browser', 'error');
                return;
            }

            if (offlineComicSaved) {
                await sendServiceWorkerMessage({ type: 'REMOVE_OFFLINE_COMIC', filePath: comicFilePath });
                offlineComicSaved = false;
                updateOfflineButton('Save offline', 'Save this comic for offline reading');
                showMessage('Removed from offline storage', 'info');
                return;
            }

            offlineDownloadInProgress = true;
            updateOfflineButton('Saving…', 'Downloading this comic for offline reading');

            // Reuse the reader's normal auth headers: the worker cannot read
            // the token out of localStorage itself.
            const headers = Object.assign({}, buildFetchOptions().headers || {});

            controller.postMessage({
                type: 'DOWNLOAD_COMIC',
                filePath: comicFilePath,
                headers: headers
            });
        }

        function handleServiceWorkerMessage(event) {
            const data = event.data;
            if (!data || data.filePath !== comicFilePath) return;

            if (data.type === 'OFFLINE_DOWNLOAD_PROGRESS') {
                const percent = data.total ? Math.round((data.completed / data.total) * 100) : 0;
                updateOfflineButton(`Saving ${percent}%`, 'Downloading this comic for offline reading');
                return;
            }

            if (data.type === 'OFFLINE_DOWNLOAD_COMPLETE') {
                offlineDownloadInProgress = false;
                offlineComicSaved = true;
                updateOfflineButton('Saved', 'Remove this comic from offline storage');
                showMessage('Available for offline reading', 'success');
                return;
            }

            if (data.type === 'OFFLINE_DOWNLOAD_FAILED') {
                offlineDownloadInProgress = false;
                offlineComicSaved = false;
                updateOfflineButton('Save offline', 'Save this comic for offline reading');
                showMessage('Offline download failed', 'error');
            }
        }

        async function setupOfflineReading() {
            if (!('serviceWorker' in navigator)) return;
            try {
                navigator.serviceWorker.addEventListener('message', handleServiceWorkerMessage);
                if (!navigator.serviceWorker.controller) {
                    // The reader can be opened directly (deep link, home-screen
                    // shortcut) without ever visiting the library page that
                    // normally registers the worker.
                    await navigator.serviceWorker.register('/sw.js', { scope: '/' });
                    await navigator.serviceWorker.ready;
                }
                await refreshOfflineStatus();
            } catch (error) {
                console.log('Offline reading unavailable:', error);
            }
        }

        // Replay queued progress as soon as the connection returns, and once
        // more when the tab is brought back to the foreground (mobile browsers
        // often deliver no 'online' event to a backgrounded tab).
        function setupProgressSync() {
            window.addEventListener('online', () => {
                flushPendingProgress();
            });
            document.addEventListener('visibilitychange', () => {
                if (!document.hidden && navigator.onLine) {
                    flushPendingProgress();
                }
            });
            if (navigator.onLine) {
                flushPendingProgress();
            }
        }

        // Initialize on load
        window.addEventListener('DOMContentLoaded', () => {
            initReader();
            setupOfflineReading();
            setupProgressSync();
        });
