const { chromium } = require('playwright');

async function runSettingsModalTest() {
    console.log('Starting Settings Modal Integration Test...\n');
    
    const browser = await chromium.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox']
    });
    
    const context = await browser.newContext({
        ignoreHTTPSErrors: true
    });
    
    const page = await context.newPage();
    
    // Listen for console errors
    const consoleErrors = [];
    page.on('console', msg => {
        if (msg.type() === 'error') {
            consoleErrors.push(msg.text());
        }
    });
    
    // Listen for page errors
    const pageErrors = [];
    page.on('pageerror', error => {
        pageErrors.push(error.message);
    });
    
    try {
        // Navigate to login page
        console.log('1. Navigating to login page...');
        await page.goto('http://localhost:5072/login.html', { waitUntil: 'networkidle' });
        
        // Login
        console.log('2. Logging in...');
        await page.fill('#username', 'testadmin');
        await page.fill('#password', 'TestPassword123!');
        await page.click('button[type="submit"]');
        
        // Wait for redirect to main page
        await page.waitForURL('**/index.html', { timeout: 5000 });
        console.log('✓ Successfully logged in');
        
        // Wait for page to load
        await page.waitForSelector('.settings-menu-toggle', { timeout: 5000 });
        console.log('✓ Main page loaded');
        
        // Open settings dropdown menu
        console.log('3. Opening settings dropdown...');
        await page.click('.settings-menu-toggle');
        await page.waitForSelector('.settings-dropdown-menu', { state: 'visible', timeout: 2000 });
        console.log('✓ Settings dropdown opened');
        
        // Click on Settings menu item
        console.log('4. Clicking Settings menu item...');
        await page.click('button.settings-dropdown-item:has-text("Settings")');
        
        // Wait for settings modal to appear
        console.log('5. Waiting for settings modal...');
        await page.waitForSelector('#settingsModal.active', { timeout: 5000 });
        console.log('✓ Settings modal opened successfully!');
        
        // Verify the log max size field is populated correctly
        console.log('6. Verifying log max size field...');
        const logMaxSizeValue = await page.inputValue('#logMaxSize');
        console.log(`   Log Max Size value: ${logMaxSizeValue} MB`);
        
        // The API returns log_max_bytes as 10485760 (10 MB in bytes)
        // With our fix, it should be converted to 10 MB
        const expectedValue = '10';  // 10485760 bytes / 1048576 = 10 MB
        
        if (logMaxSizeValue === expectedValue) {
            console.log(`✓ Log max size is correctly converted: ${logMaxSizeValue} MB`);
        } else {
            console.log(`✗ Log max size conversion failed:`);
            console.log(`   Expected: ${expectedValue} MB`);
            console.log(`   Got: ${logMaxSizeValue} MB`);
            
            // Check if it's the buggy value (bytes instead of MB)
            if (logMaxSizeValue === '10485760') {
                console.log(`   ERROR: Value is in bytes, not MB (bug not fixed)`);
            }
        }
        
        // Take a screenshot
        console.log('7. Taking screenshot...');
        await page.screenshot({ path: '/tmp/settings-modal-open.png', fullPage: true });
        console.log('✓ Screenshot saved to /tmp/settings-modal-open.png');
        
        // Check for any console or page errors
        if (consoleErrors.length > 0) {
            console.log('\n⚠️  Console Errors:');
            consoleErrors.forEach(err => console.log(`   - ${err}`));
        }
        
        if (pageErrors.length > 0) {
            console.log('\n⚠️  Page Errors:');
            pageErrors.forEach(err => console.log(`   - ${err}`));
        }
        
        if (consoleErrors.length === 0 && pageErrors.length === 0 && logMaxSizeValue === expectedValue) {
            console.log('\n✅ All tests passed! Settings modal opens correctly.');
            await browser.close();
            process.exit(0);
        } else {
            console.log('\n❌ Some tests failed.');
            await browser.close();
            process.exit(1);
        }
        
    } catch (error) {
        console.error('\n❌ Test failed with error:', error.message);
        
        // Take error screenshot
        try {
            await page.screenshot({ path: '/tmp/settings-modal-error.png', fullPage: true });
            console.log('Error screenshot saved to /tmp/settings-modal-error.png');
        } catch (e) {
            // Ignore screenshot errors
        }
        
        await browser.close();
        process.exit(1);
    }
}

runSettingsModalTest();
