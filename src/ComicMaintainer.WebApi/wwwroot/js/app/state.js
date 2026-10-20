        // ComicMaintainer SPA — shared library/view state and constants.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Constants
        const DEFAULT_PER_PAGE = 100; // Default number of items per page
        const LIBRARY_HEALTH_REFRESH_DELAY = 750;
        const FILE_LIST_REFRESH_DEBOUNCE_DELAY = 500;
        const MAX_PROGRESS_RESULTS = 200;
        
        // Filename truncation constants
        const FILENAME_TRUNCATION_START_LENGTH = 30; // Characters to show at the start
        const FILENAME_TRUNCATION_END_LENGTH = 15; // Characters to show at the end (includes extension)
        const MAX_EXTENSION_LENGTH = 10; // Maximum length to consider as a valid extension
        const TRUNCATION_ELLIPSIS = '...'; // String to indicate truncation
        
        let files = [];
        let selectedFiles = new Set();
        // Series selected in the series list view. Selecting a series adds all
        // of its file paths to `selectedFiles` so the existing "process /
        // rename / normalize selected" actions operate on those files.
        let selectedSeries = new Set();
        // seriesId -> string[] of the file paths contributed to selectedFiles,
        // so a deselect can remove exactly the paths a series added.
        let selectedSeriesFilePaths = new Map();
        let selectedSeriesPathLoads = new Map();
        // File path of the issue whose checkbox was clicked last in the series
        // detail view. It anchors a shift-click range selection.
        let lastSelectedIssuePath = null;
        // True while an issue checkbox is being toggled with Shift held.
        let seriesIssueRangeSelectArmed = false;
        let currentEditFile = null;
        let collapsedDirectories = new Set();
        let searchQuery = '';
        let allFoldersExpanded = false;
        let currentPage = 1;
        let totalPages = 1;
        let totalFiles = 0;
        let unmarkedCount = 0;
        let perPage = DEFAULT_PER_PAGE; // Will be loaded from server preferences
        let filterMode = 'all'; // 'all', 'marked', 'unmarked', 'duplicates'
        // Single source of truth for header-filter button labels. Used both when
        // restoring the saved filter on load and when the user changes it, so the
        // label stays correct for every filter mode (previously the restore path
        // omitted renamed/normalized/read/unread and showed a blank label).
        const FILTER_LABELS = {
            'all': 'All',
            'unmarked': 'Unmarked',
            'marked': 'Marked',
            'duplicates': 'Duplicates',
            'renamed': 'Renamed',
            'normalized': 'Normalized',
            'read': 'Read',
            'unread': 'Unread',
            'matched': 'Matched',
            'unmatched': 'Not Matched',
            'missing': 'Missing Issues'
        };
        let libraryViewMode = 'series';
        // Series layout: null = auto (list on mobile portrait, compact otherwise),
        // or one of 'list', 'grid', 'compact' when explicitly chosen by the user.
        let seriesLayoutPreference = null;
        try {
            const stored = localStorage.getItem('seriesLayout');
            if (stored === 'list' || stored === 'grid' || stored === 'compact') {
                seriesLayoutPreference = stored;
            }
        } catch (_e) { /* localStorage may be unavailable */ }
        let seriesLibrary = [];
        // Incremental library view state
        const FOLDER_PAGE_SIZE = 100;
        const SERIES_PAGE_SIZE = 60;
        let folderList = [];
        let folderTotal = 0;
        let folderOffset = 0;
        let folderLoading = false;
        // Set when a fresh (non-append) folder reload is requested while another
        // load is in flight, so we re-fetch with the latest filter/search/sort
        // once the in-flight request settles instead of dropping the request.
        let folderReloadPending = false;
        const folderFiles = new Map(); // path -> { status: 'loading'|'loaded'|'error', files: [] }
        let seriesOffset = 0;
        let seriesTotal = 0;
        let seriesLoading = false;
        // Set when a fresh (non-append) series reload is requested while another
        // load is in flight (e.g. the user changes the filter twice in quick
        // succession). The in-flight request captured the previous filter, so we
        // must re-fetch once it settles; otherwise the list stays on the old
        // filter (label updated, content stale) until a manual page refresh.
        let seriesReloadPending = false;
        let scrollObserver = null;
        let currentSeriesDetailId = null;
        // Title-based identity for the open series detail. The series id is a
        // union-find representative key whose value can change when an external
        // metadata refresh introduces new aliases — when that happens we use
        // these title keys to find the same series under its new id so the user
        // stays inside the detail view instead of getting kicked back to the
        // library list.
        let currentSeriesDetailTitleKeys = null;
        // Snapshot of the open series' lightweight metadata (id, title, aliases,
        // cover, issue_count, total_size, metadata_source). Updated whenever
        // the series is found in the library list or refreshed via the issues
        // endpoint. Used as a fallback when the active filter excludes the
        // series from the library list so the user is not kicked back to the
        // series list while drilled into a series.
        let currentSeriesDetailSeries = null;
        const seriesIssuesCache = new Map();        // seriesId -> { issues: [], total: n }
        const metadataRefreshJobs = new Map();     // jobId -> { seriesIds, label }
        let providerHealthRefreshTimer = null;
        let searchDebounceTimer = null;
        let historyCurrentPage = 1;
        let historyPerPage = 50;
        let historyTotal = 0;
        let libraryHealthRefreshTimer = null;
        let libraryHealthRequestInFlight = false;
        let libraryHealthRefreshPending = false;
        let fileListRefreshTimer = null;
        const MOBILE_LIBRARY_VIEW_BREAKPOINT = 768; // Matches the existing mobile CSS breakpoint.
        const DEFAULT_MOBILE_LIBRARY_VIEW = 'files';
        let currentMobileLibraryView = DEFAULT_MOBILE_LIBRARY_VIEW;
        let progressResults = [];
        let progressResultLookup = new Set();
        let progressResultElements = new Map();
        let duplicateReviewFiles = [];
        let duplicateReviewIndex = 0;
        let duplicateReviewLoadId = 0;
        let combineFolderGroups = [];
        let combineFolderIndex = 0;
        let combineFolderSelectedDestination = null;
        let combineFolderActionInFlight = false;
        let protectedImageUrls = new Map();
        const MAX_PROTECTED_IMAGE_CACHE_ENTRIES = 150;

        // Stable sentinel filepath used to drive the existing toggleDropdown /
        // getDropdownId helpers for the single per-series actions dropdown
        // rendered in the series detail view.
        const SERIES_ACTIONS_DROPDOWN_KEY = '__series_actions__';
