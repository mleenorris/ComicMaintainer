// ComicMaintainer sign-in page (login.html).
        //
        // Classic (non-module) script sharing the page's global scope: the
        // markup's inline on* handlers call its functions.

        // Base path for reverse proxy support
        const BASE_PATH = '';
        
        // Helper function to build URLs with base path
        function apiUrl(path) {
            if (!path.startsWith('/')) {
                path = '/' + path;
            }
            return BASE_PATH + path;
        }
        
        // Theme management
        function initTheme() {
            const theme = localStorage.getItem('theme') || 'light';
            document.documentElement.setAttribute('data-theme', theme);
        }
        
        function toggleTheme() {
            const currentTheme = document.documentElement.getAttribute('data-theme');
            const newTheme = currentTheme === 'dark' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-theme', newTheme);
            localStorage.setItem('theme', newTheme);
        }
        
        // Hide timers of the messages below. Showing a message again cancels
        // its earlier timer, which would otherwise hide the new one early.
        let errorHideTimer = null;
        let successHideTimer = null;
        
        // Show error message
        function showError(message) {
            const errorElement = document.getElementById('errorMessage');
            // The element is display:none until '.show' is added, which keeps it
            // out of the accessibility tree. Reveal it first so the text lands in
            // a live region that is already exposed, otherwise screen readers may
            // not announce the failure at all.
            errorElement.classList.add('show');
            errorElement.textContent = message;
            
            clearTimeout(errorHideTimer);
            errorHideTimer = setTimeout(() => {
                errorElement.classList.remove('show');
                errorElement.textContent = '';
            }, 5000);
        }
        
        // Show success message
        function showSuccess(message) {
            const successElement = document.getElementById('successMessage');
            successElement.classList.add('show');
            successElement.textContent = message;
            
            clearTimeout(successHideTimer);
            successHideTimer = setTimeout(() => {
                successElement.classList.remove('show');
                successElement.textContent = '';
            }, 3000);
        }
        
        // Helper function to decode JWT token and extract expiration
        function decodeJwtToken(token) {
            try {
                // JWT format: header.payload.signature
                const parts = token.split('.');
                if (parts.length !== 3) {
                    return null;
                }
                
                // Decode the payload (second part)
                const payload = parts[1];
                // Add padding if needed for base64 decode
                const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
                const paddedBase64 = base64.padEnd(base64.length + (4 - base64.length % 4) % 4, '=');
                const jsonPayload = decodeURIComponent(atob(paddedBase64).split('').map(function(c) {
                    return '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2);
                }).join(''));
                
                return JSON.parse(jsonPayload);
            } catch (error) {
                console.error('Error decoding JWT token:', error);
                return null;
            }
        }
        
        // Helper function to check if JWT token is expired
        function isTokenExpired(token) {
            const decoded = decodeJwtToken(token);
            if (!decoded || !decoded.exp) {
                return true; // Treat invalid token as expired
            }
            
            // JWT exp claim is in seconds, Date.now() is in milliseconds
            const expirationTime = decoded.exp * 1000;
            const currentTime = Date.now();
            
            return currentTime >= expirationTime;
        }
        
        // Check if setup is required. Resolves to true when it is, and the
        // page is on its way to the setup page.
        async function checkSetupRequired() {
            try {
                const response = await fetch(apiUrl('/api/auth/setup-required'));
                const data = await response.json();
                
                if (data.setupRequired) {
                    // Setup is required, redirect to setup page
                    window.location.href = '/setup.html';
                    return true;
                }
            } catch (error) {
                console.error('Error checking setup status:', error);
            }
            return false;
        }
        
        // Check if already logged in (including Authelia)
        async function checkAuth() {
            try {
                // First check server auth status (includes Authelia check)
                const response = await fetch(apiUrl('/api/auth/status'), {
                    credentials: 'include' // Important for Authelia cookies
                });
                
                if (response.ok) {
                    const authStatus = await response.json();
                    console.log('[LOGIN] Server auth status:', authStatus);
                    
                    // If Authelia is enabled and user is authenticated, redirect to main page
                    if (authStatus.autheliaEnabled && authStatus.isAuthenticated) {
                        console.log('[LOGIN] Already authenticated via Authelia as:', authStatus.username);
                        localStorage.setItem('authelia_authenticated', 'true');
                        localStorage.setItem('username', authStatus.username);
                        window.location.href = '/';
                        return;
                    }
                }
            } catch (error) {
                console.error('[LOGIN] Error checking auth status:', error);
            }
            
            // Check JWT token
            const token = localStorage.getItem('jwt_token');
            if (token) {
                // Check if token is expired
                if (isTokenExpired(token)) {
                    // Token expired, clear it and stay on login page
                    console.log('JWT token has expired, clearing storage');
                    localStorage.removeItem('jwt_token');
                    localStorage.removeItem('username');
                    return;
                }
                // Token is valid, redirect to main page
                window.location.href = '/';
            }
        }
        
        // Handle login form submission
        document.getElementById('loginForm').addEventListener('submit', async (e) => {
            e.preventDefault();
            
            const username = document.getElementById('username').value.trim();
            const password = document.getElementById('password').value;
            
            if (!username || !password) {
                showError('Please enter both username and password');
                return;
            }
            
            // Show loading state
            const loginBtn = document.getElementById('loginBtn');
            const loginBtnText = document.getElementById('loginBtnText');
            const loginBtnSpinner = document.getElementById('loginBtnSpinner');
            
            loginBtn.disabled = true;
            loginBtnText.classList.add('hidden');
            loginBtnSpinner.classList.remove('hidden');
            
            // Set once signed in: the button then stays disabled until the page
            // navigates, so a second click cannot sign in again.
            let redirecting = false;
            
            try {
                const response = await fetch(apiUrl('/api/auth/login'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ username, password })
                });
                
                const data = await response.json();
                
                if (response.ok && data.token) {
                    // Store the token
                    localStorage.setItem('jwt_token', data.token);
                    localStorage.setItem('username', username);
                    
                    // Show success and redirect
                    showSuccess('Login successful! Redirecting...');
                    redirecting = true;
                    
                    setTimeout(() => {
                        window.location.href = '/';
                    }, 500);
                } else {
                    showError(data.error || 'Login failed. Please check your credentials.');
                }
            } catch (error) {
                console.error('Login error:', error);
                showError('An error occurred. Please try again.');
            } finally {
                // Reset button state, unless the page is about to navigate
                if (!redirecting) {
                    loginBtn.disabled = false;
                    loginBtnText.classList.remove('hidden');
                    loginBtnSpinner.classList.add('hidden');
                }
            }
        });
        
        // Initialize theme on page load
        initTheme();
        
        // Check if setup is required first, and only when it is not whether the
        // user is already authenticated: either check can redirect, and the
        // page must redirect only once.
        checkSetupRequired().then(setupRequired => {
            if (!setupRequired) {
                checkAuth();
            }
        });
