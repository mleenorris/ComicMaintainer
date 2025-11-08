const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

async function runIntegrationTest() {
    console.log('Starting Settings Modal Integration Test...\n');
    
    const browser = await chromium.launch({
        headless: true
    });
    
    const context = await browser.newContext();
    const page = await context.newPage();
    
    // Navigate to the test HTML file
    const testFilePath = path.join(__dirname, 'settings-modal.test.html');
    await page.goto(`file://${testFilePath}`);
    
    // Wait for tests to complete
    await page.waitForSelector('[data-test-complete="true"]', { timeout: 5000 });
    
    // Get test results
    const testResults = await page.evaluate(() => window.testResults);
    
    // Display results
    console.log('Test Results:');
    console.log('='.repeat(80));
    
    testResults.results.forEach((result, index) => {
        const status = result.pass ? '✓ PASS' : '✗ FAIL';
        const color = result.pass ? '\x1b[32m' : '\x1b[31m';
        const reset = '\x1b[0m';
        
        console.log(`${color}${status}${reset}: ${result.name}`);
        console.log(`     Expected: ${result.expected}, Got: ${result.actual}`);
    });
    
    console.log('='.repeat(80));
    console.log(`\nSummary: ${testResults.passed}/${testResults.total} tests passed`);
    
    // Take a screenshot
    const screenshotPath = path.join(__dirname, 'settings-modal-test-result.png');
    await page.screenshot({ path: screenshotPath, fullPage: true });
    console.log(`\nScreenshot saved to: ${screenshotPath}`);
    
    await browser.close();
    
    // Exit with appropriate code
    process.exit(testResults.passed === testResults.total ? 0 : 1);
}

runIntegrationTest().catch(error => {
    console.error('Test failed with error:', error);
    process.exit(1);
});
