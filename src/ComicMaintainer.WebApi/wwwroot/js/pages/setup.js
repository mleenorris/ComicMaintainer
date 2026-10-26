// ComicMaintainer first-run setup page (setup.html) — creates the administrator account.
        //
        // Classic (non-module) script sharing the page's global scope: the
        // markup's inline on* handlers call its functions.

        // Security Note: Ensure this application is deployed with HTTPS in production
        // to protect credentials during transmission.
        
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
        
        // Show error message
        function showError(message) {
            const errorElement = document.getElementById('errorMessage');
            // The element is display:none until '.show' is added, which keeps it
            // out of the accessibility tree. Reveal it first so the text lands in
            // a live region that is already exposed, otherwise screen readers may
            // not announce the failure at all.
            errorElement.classList.add('show');
            errorElement.textContent = message;
            
            setTimeout(() => {
                errorElement.classList.remove('show');
                errorElement.textContent = '';
            }, 5000);
        }
        
        // Show success message
        function showSuccess(message) {
            const successElement = document.getElementById('successMessage');
            successElement.classList.add('show');
            successElement.textContent = message;
            
            setTimeout(() => {
                successElement.classList.remove('show');
                successElement.textContent = '';
            }, 3000);
        }
        
        // Check password strength
        function checkPasswordStrength(password) {
            const strengthElement = document.getElementById('passwordStrength');
            let strength = 0;
            
            if (password.length >= 8) strength++;
            if (password.length >= 12) strength++;
            if (/[a-z]/.test(password) && /[A-Z]/.test(password)) strength++;
            if (/\d/.test(password)) strength++;
            if (/[^a-zA-Z0-9]/.test(password)) strength++;
            
            strengthElement.classList.remove('weak', 'medium', 'strong');
            
            if (strength <= 2) {
                strengthElement.classList.add('weak');
            } else if (strength <= 4) {
                strengthElement.classList.add('medium');
            } else {
                strengthElement.classList.add('strong');
            }
        }
        
        // Password strength indicator
        document.getElementById('password').addEventListener('input', (e) => {
            checkPasswordStrength(e.target.value);
        });
        
        // Check if setup is still required
        async function checkSetupRequired() {
            try {
                const response = await fetch(apiUrl('/api/auth/setup-required'));
                const data = await response.json();
                
                if (!data.setupRequired) {
                    // Setup already completed, redirect to login
                    window.location.href = '/login.html';
                }
            } catch (error) {
                console.error('Error checking setup status:', error);
            }
        }
        
        // Handle setup form submission
        document.getElementById('setupForm').addEventListener('submit', async (e) => {
            e.preventDefault();
            
            const username = document.getElementById('username').value.trim();
            const password = document.getElementById('password').value;
            const confirmPassword = document.getElementById('confirmPassword').value;
            
            if (!username || !password || !confirmPassword) {
                showError('Please fill in all fields');
                return;
            }
            
            if (username.length < 3) {
                showError('Username must be at least 3 characters');
                return;
            }
            
            if (password.length < 8) {
                showError('Password must be at least 8 characters');
                return;
            }
            
            if (password !== confirmPassword) {
                showError('Passwords do not match');
                return;
            }
            
            // Show loading state
            const setupBtn = document.getElementById('setupBtn');
            const setupBtnText = document.getElementById('setupBtnText');
            const setupBtnSpinner = document.getElementById('setupBtnSpinner');
            
            setupBtn.disabled = true;
            setupBtnText.classList.add('hidden');
            setupBtnSpinner.classList.remove('hidden');
            
            try {
                const response = await fetch(apiUrl('/api/auth/setup'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ 
                        username, 
                        password,
                        email: null
                    })
                });
                
                const data = await response.json();
                
                if (response.ok) {
                    // Show success and redirect to login
                    showSuccess('Administrator account created successfully! Redirecting to login...');
                    
                    setTimeout(() => {
                        window.location.href = '/login.html';
                    }, 1500);
                } else {
                    showError(data.error || 'Setup failed. Please try again.');
                    
                    // Reset button state
                    setupBtn.disabled = false;
                    setupBtnText.classList.remove('hidden');
                    setupBtnSpinner.classList.add('hidden');
                }
            } catch (error) {
                console.error('Setup error:', error);
                showError('An error occurred. Please try again.');
                
                // Reset button state
                setupBtn.disabled = false;
                setupBtnText.classList.remove('hidden');
                setupBtnSpinner.classList.add('hidden');
            }
        });
        
        // Initialize theme on page load
        initTheme();
        
        // Check if setup is still required
        checkSetupRequired();
