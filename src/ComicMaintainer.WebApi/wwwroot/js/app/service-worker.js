        // ComicMaintainer SPA — service worker registration, update banner and PWA install prompt.
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // Load files on page load
        // PWA Installation support
        let deferredPrompt = null;
        
        // Tracks the worker the user has been told about, so repeated
        // 'updatefound' events don't stack up multiple banners.
        let pendingServiceWorkerUpdate = null;

        // How long the update banner's Reload button waits for the new worker
        // to take control before reloading anyway.
        const SW_UPDATE_RELOAD_TIMEOUT_MS = 5000;

        let serviceWorkerReloadStarted = false;

        // Reload onto a new version, at most once: when the update banner gives
        // up waiting, or on 'controllerchange' (the new worker is in control,
        // so it serves the reloaded page).
        function reloadForServiceWorkerUpdate(reason) {
            if (serviceWorkerReloadStarted) return;
            serviceWorkerReloadStarted = true;
            console.log(`PWA: ${reason}, reloading page...`);
            window.location.reload();
        }

        /**
         * Ask a waiting service worker to take over, then reload. The browser
         * only swaps workers once the current one is idle, so SKIP_WAITING alone
         * can wait indefinitely; this must always end in a reload.
         */
        function applyServiceWorkerUpdate(worker) {
            // A worker from an older release still proxies the real-time event
            // stream. That request never finishes, so while this tab holds it
            // open the current worker is never idle and cannot be replaced.
            cleanupEventSource();

            if (worker.state === 'activated' || worker.state === 'redundant') {
                // Already in control, or replaced by an even newer version:
                // there is nothing left to wait for.
                reloadForServiceWorkerUpdate(`Service worker is ${worker.state}`);
                return;
            }

            worker.addEventListener('statechange', () => {
                if (worker.state === 'activated' || worker.state === 'redundant') {
                    reloadForServiceWorkerUpdate(`Service worker is ${worker.state}`);
                }
            });
            worker.postMessage({ type: 'SKIP_WAITING' });

            // Another tab can still be keeping the old worker busy. Reload
            // anyway rather than leave the button on "Reloading…": documents
            // are fetched network-first and assets carry the version in their
            // URL, so the reloaded page runs the new release regardless.
            setTimeout(() => {
                reloadForServiceWorkerUpdate('New service worker did not take control in time');
            }, SW_UPDATE_RELOAD_TIMEOUT_MS);
        }

        /**
         * Show a dismissible banner offering to activate a newly installed
         * service worker. Activation (and the reload that follows it via
         * 'controllerchange') only happens when the user opts in.
         */
        function promptForServiceWorkerUpdate(worker) {
            if (!worker || pendingServiceWorkerUpdate === worker) {
                return;
            }
            pendingServiceWorkerUpdate = worker;

            const existing = document.getElementById('appUpdateBanner');
            if (existing) {
                existing.remove();
            }

            const banner = document.createElement('div');
            banner.id = 'appUpdateBanner';
            banner.className = 'app-update-banner';
            banner.setAttribute('role', 'status');
            banner.setAttribute('aria-live', 'polite');

            const text = document.createElement('span');
            text.className = 'app-update-banner-text';
            text.textContent = 'A new version of Comic Maintainer is available.';

            const reloadBtn = document.createElement('button');
            reloadBtn.type = 'button';
            reloadBtn.className = 'btn btn-primary btn-small';
            reloadBtn.textContent = 'Reload';
            reloadBtn.addEventListener('click', () => {
                reloadBtn.disabled = true;
                reloadBtn.textContent = 'Reloading…';
                applyServiceWorkerUpdate(worker);
            });

            const dismissBtn = document.createElement('button');
            dismissBtn.type = 'button';
            dismissBtn.className = 'app-update-banner-dismiss';
            dismissBtn.setAttribute('aria-label', 'Dismiss update notification');
            dismissBtn.textContent = '\u00d7';
            dismissBtn.addEventListener('click', () => {
                banner.remove();
            });

            banner.append(text, reloadBtn, dismissBtn);
            document.body.appendChild(banner);
        }

        // Register service worker for offline support
        if ('serviceWorker' in navigator) {
            window.addEventListener('load', () => {
                navigator.serviceWorker.register(apiUrl('/sw.js'), {
                    scope: apiUrl('/')
                })
                    .then((registration) => {
                        console.log('PWA: Service Worker registered successfully:', registration.scope);

                        // Check for updates periodically. The poll is skipped
                        // while the tab is hidden: a background tab has nobody
                        // to show an update banner to, and a pinned tab left
                        // open for days would otherwise keep issuing a request
                        // a minute forever. Becoming visible again triggers a
                        // check so a returning user is not left waiting up to a
                        // minute for the banner.
                        //
                        // Both paths go through the same minimum interval. The
                        // registration is not cached (sw.js is served no-store
                        // and the main script always bypasses the HTTP cache),
                        // so every update() is a real request — without the
                        // guard, an actively used tab would issue one per
                        // focus change and end up noisier than the plain
                        // interval this replaced.
                        const SW_UPDATE_INTERVAL_MS = 60000;
                        let lastServiceWorkerUpdateCheck = 0;
                        const checkForUpdate = () => {
                            if (document.hidden) return;
                            const now = Date.now();
                            if (now - lastServiceWorkerUpdateCheck < SW_UPDATE_INTERVAL_MS) return;
                            lastServiceWorkerUpdateCheck = now;
                            registration.update().catch(err => {
                                console.debug('PWA: Service Worker update check failed:', err);
                            });
                        };
                        setInterval(checkForUpdate, SW_UPDATE_INTERVAL_MS);
                        document.addEventListener('visibilitychange', () => {
                            if (!document.hidden) checkForUpdate();
                        });

                        // When a new service worker has fully installed, surface
                        // it to the user instead of silently activating and
                        // reloading. An unannounced reload can interrupt work in
                        // progress (an open settings form, a running batch job's
                        // progress view), so the swap is deferred until the user
                        // asks for it.
                        //
                        // The reload is normally driven by 'controllerchange'
                        // below, which is the documented signal that the new
                        // worker is in control, so the reloaded page is served by
                        // it. applyServiceWorkerUpdate() only reloads without that
                        // signal as a last resort, when another tab keeps the old
                        // worker busy.
                        if (registration.waiting && navigator.serviceWorker.controller) {
                            // A new version finished installing during a previous
                            // visit and has been waiting ever since.
                            promptForServiceWorkerUpdate(registration.waiting);
                        }

                        registration.addEventListener('updatefound', () => {
                            const newWorker = registration.installing;
                            console.log('PWA: New service worker installing...');

                            if (!newWorker) {
                                return;
                            }

                            newWorker.addEventListener('statechange', () => {
                                if (newWorker.state !== 'installed') {
                                    return;
                                }

                                if (!navigator.serviceWorker.controller) {
                                    // First ever install: nothing is cached yet and
                                    // there is no stale page to replace, so there is
                                    // nothing to prompt about.
                                    console.log('PWA: Service worker installed for the first time');
                                    return;
                                }

                                console.log('PWA: New version installed, prompting user to reload');
                                promptForServiceWorkerUpdate(newWorker);
                            });
                        });
                    })
                    .catch((error) => {
                        console.log('PWA: Service Worker registration failed:', error);
                    });

                // Reload exactly once the new service worker has taken control,
                // so the reloaded page is served by the NEW worker (and thus the
                // new cached assets).
                navigator.serviceWorker.addEventListener('controllerchange', () => {
                    reloadForServiceWorkerUpdate('New service worker took control');
                });
            });
        }
        
        // Listen for the beforeinstallprompt event
        window.addEventListener('beforeinstallprompt', (e) => {
            console.log('PWA: beforeinstallprompt event fired');
            // Prevent the default mini-infobar from appearing on mobile
            e.preventDefault();
            // Stash the event so we can trigger it later via our custom install button
            deferredPrompt = e;
            setInstallAppButtonsVisible(true);
        });
        
        // Handle successful installation
        window.addEventListener('appinstalled', (evt) => {
            console.log('PWA: App successfully installed');
            setInstallAppButtonsVisible(false);
            deferredPrompt = null;
        });

        function setInstallAppButtonsVisible(visible) {
            ['installAppButton', 'installAppSidebarButton'].forEach(id => {
                const button = document.getElementById(id);
                if (!button) return;
                button.style.display = visible
                    ? (id === 'installAppSidebarButton' ? 'flex' : 'block')
                    : 'none';
            });
        }
        
        // Function to trigger installation
        function installApp() {
            if (!deferredPrompt) {
                showMessage('App is already installed or installation is not available.', 'info');
                return;
            }
            
            // Show the install prompt
            deferredPrompt.prompt();
            
            // Wait for the user to respond to the prompt
            deferredPrompt.userChoice.then((choiceResult) => {
                if (choiceResult.outcome === 'accepted') {
                    console.log('PWA: User accepted the install prompt');
                } else {
                    console.log('PWA: User dismissed the install prompt');
                }
                deferredPrompt = null;
            });
        }
