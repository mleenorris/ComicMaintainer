// Service Worker for Comic Maintainer PWA
// Provides basic offline support and caching
// Version is dynamically injected by the server to force browser updates

// Cache version will be determined dynamically from the API
let CACHE_NAME = 'comic-maintainer-v2'; // Default fallback
const CACHE_PREFIX = 'comic-maintainer-';
const urlsToCache = [
  '/',
  '/manifest.json',
  '/icons/icon-192x192.png',
  '/icons/icon-512x512.png',
  '/icons/icon-192x192-maskable.png',
  '/icons/icon-512x512-maskable.png',
  '/icons/apple-touch-icon.png',
  '/icons/favicon-32x32.png',
  '/icons/favicon-16x16.png'
];

// Fetch current version from API and set cache name
async function updateCacheName() {
  try {
    const response = await fetch('/api/version');
    if (response.ok) {
      const data = await response.json();
      const version = data.version.replace(/\./g, '-'); // Replace dots with dashes for cache name
      CACHE_NAME = `${CACHE_PREFIX}${version}`;
      console.log('Service Worker: Using cache version:', CACHE_NAME);
    }
  } catch (error) {
    console.log('Service Worker: Failed to fetch version, using default cache name', error);
  }
}

// Install event - cache essential resources
self.addEventListener('install', (event) => {
  console.log('Service Worker: Installing...');
  event.waitUntil(
    updateCacheName()
      .then(() => caches.open(CACHE_NAME))
      .then((cache) => {
        console.log('Service Worker: Caching essential files with cache name:', CACHE_NAME);
        return cache.addAll(urlsToCache);
      })
      .then(() => self.skipWaiting())
  );
});

// Activate event - clean up old caches
self.addEventListener('activate', (event) => {
  console.log('Service Worker: Activating...');
  event.waitUntil(
    updateCacheName()
      .then(() => caches.keys())
      .then((cacheNames) => {
        return Promise.all(
          cacheNames.map((cacheName) => {
            // Delete any cache that starts with our prefix but isn't the current version
            if (cacheName.startsWith(CACHE_PREFIX) && cacheName !== CACHE_NAME) {
              console.log('Service Worker: Deleting old cache:', cacheName);
              return caches.delete(cacheName);
            }
          })
        );
      }).then(() => self.clients.claim())
  );
});

// Fetch event - serve from cache when offline, with network-first strategy for API calls
self.addEventListener('fetch', (event) => {
  const { request } = event;
  const url = new URL(request.url);
  
  // Never cache the service worker file itself or HTML pages
  if (url.pathname === '/sw.js' || url.pathname.endsWith('.html')) {
    event.respondWith(fetch(request));
    return;
  }
  
  // Network-first strategy for API calls and dynamic content
  if (url.pathname.startsWith('/api/')) {
    event.respondWith(
      fetch(request)
        .then((response) => {
          // Don't cache API responses (they're dynamic)
          return response;
        })
        .catch(() => {
          // Return a friendly offline message for API calls
          return new Response(
            JSON.stringify({ error: 'Offline - API unavailable' }),
            {
              status: 503,
              headers: { 'Content-Type': 'application/json' }
            }
          );
        })
    );
    return;
  }
  
  // Cache-first strategy for static assets
  event.respondWith(
    caches.match(request)
      .then((response) => {
        if (response) {
          // Return cached version
          return response;
        }
        
        // Not in cache, fetch from network
        return fetch(request).then((response) => {
          // Don't cache non-successful responses
          if (!response || response.status !== 200 || response.type !== 'basic') {
            return response;
          }
          
          // Clone the response (can only be consumed once)
          const responseToCache = response.clone();
          
          // Cache static assets
          if (url.pathname.startsWith('/icons/') || url.pathname.startsWith('/css/') || url.pathname.startsWith('/js/') || url.pathname === '/') {
            caches.open(CACHE_NAME).then((cache) => {
              cache.put(request, responseToCache);
            });
          }
          
          return response;
        });
      })
      .catch(() => {
        // Return a friendly offline page for HTML requests
        if (request.headers.get('Accept').includes('text/html')) {
          return new Response(
            '<html><body><h1>Offline</h1><p>Comic Maintainer is unavailable while offline.</p></body></html>',
            {
              headers: { 'Content-Type': 'text/html' }
            }
          );
        }
      })
  );
});

// Listen for messages from the client
self.addEventListener('message', (event) => {
  if (event.data && event.data.type === 'SKIP_WAITING') {
    self.skipWaiting();
  }
});
