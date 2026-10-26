// ComicMaintainer offline fallback page (offline.html).
        //
        // The service worker serves offline.html when a navigation fails, so
        // this script must work without network access: sw.js precaches it
        // (PAGE_SCRIPTS) and it only talks to the service worker.

        (function () {
            var status = document.getElementById('statusText');
            var retry = document.getElementById('retryButton');
            var downloads = document.getElementById('downloads');
            var downloadsList = document.getElementById('downloadsList');

            function render() {
                status.textContent = navigator.onLine
                    ? 'Connection restored — try again.'
                    : 'Waiting for a network connection…';
            }

            // Comics the user downloaded are stored by the service worker, so
            // they can be listed and opened here without any network access.
            function renderDownloads(comics) {
                if (!comics || !comics.length) {
                    return;
                }
                comics
                    .slice()
                    .sort(function (a, b) {
                        return a.filePath.localeCompare(b.filePath);
                    })
                    .forEach(function (comic) {
                        var item = document.createElement('li');
                        var link = document.createElement('a');
                        link.href = '/reader.html?file=' + encodeURIComponent(comic.filePath);
                        var segments = comic.filePath.split(/[\\/]/);
                        link.textContent = segments[segments.length - 1] || comic.filePath;
                        var pages = document.createElement('span');
                        pages.className = 'pages';
                        pages.textContent = comic.pageCount === 1
                            ? '1 page saved'
                            : comic.pageCount + ' pages saved';
                        link.appendChild(pages);
                        item.appendChild(link);
                        downloadsList.appendChild(item);
                    });
                downloads.hidden = false;
            }

            function loadDownloads() {
                if (!('serviceWorker' in navigator) || !navigator.serviceWorker.controller) {
                    return;
                }
                var channel = new MessageChannel();
                channel.port1.onmessage = function (event) {
                    if (event.data && event.data.type === 'OFFLINE_COMICS') {
                        renderDownloads(event.data.comics);
                    }
                };
                navigator.serviceWorker.controller.postMessage({ type: 'LIST_OFFLINE_COMICS' }, [channel.port2]);
            }

            retry.addEventListener('click', function () {
                window.location.reload();
            });

            window.addEventListener('online', render);
            window.addEventListener('offline', render);
            render();
            loadDownloads();
        })();
