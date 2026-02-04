/**
 * Test the device flow OAuth
 */

import { chromium } from 'playwright';

const WORKER_URL = 'https://youtube-extractor.ref12cf.workers.dev';

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function testDeviceFlow() {
  console.log('🚀 Starting Device Flow test...\n');
  
  const browser = await chromium.launch({
    headless: false,
    slowMo: 300,
  });
  
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
  });
  
  const page = await context.newPage();
  
  // Log console messages
  page.on('console', msg => {
    if (msg.type() === 'error') {
      console.log(`❌ PAGE ERROR: ${msg.text()}`);
    }
  });
  
  // Log network responses
  page.on('response', response => {
    const status = response.status();
    const url = response.url();
    if (url.includes('device')) {
      console.log(`📡 ${status} ${url}`);
    }
  });
  
  try {
    // Step 1: Go to login page
    console.log('📍 Step 1: Navigating to /login...');
    await page.goto(`${WORKER_URL}/login`, { waitUntil: 'networkidle' });
    await page.screenshot({ path: 'test-device-1-login.png' });
    console.log('📸 Screenshot: test-device-1-login.png');
    
    // Step 2: Click on Device Flow option
    console.log('\n📍 Step 2: Looking for Device Flow button...');
    const deviceButton = await page.$('a[href="/login?method=device"]');
    if (!deviceButton) {
      console.log('❌ Device Flow button not found!');
      const content = await page.content();
      console.log('Page content preview:', content.substring(0, 1000));
      return;
    }
    console.log('✓ Found Device Flow button');
    await deviceButton.click();
    console.log('✓ Clicked Device Flow button');
    
    // Wait for the page to load and get device code
    await page.waitForTimeout(3000);
    await page.screenshot({ path: 'test-device-2-getting-code.png' });
    console.log('📸 Screenshot: test-device-2-getting-code.png');
    
    // Step 3: Look for the user code
    console.log('\n📍 Step 3: Looking for user code...');
    
    // Wait for code to appear
    await page.waitForTimeout(2000);
    
    const userCodeEl = await page.$('#userCode');
    if (userCodeEl) {
      const userCode = await userCodeEl.textContent();
      console.log(`\n✓ GOT USER CODE: ${userCode}\n`);
      console.log('====================================');
      console.log(`   Go to: https://google.com/device`);
      console.log(`   Enter: ${userCode}`);
      console.log('====================================\n');
      
      await page.screenshot({ path: 'test-device-3-code-displayed.png' });
      console.log('📸 Screenshot: test-device-3-code-displayed.png');
      
      // Check for verification URL
      const verificationUrl = await page.$('.verification-url a');
      if (verificationUrl) {
        const href = await verificationUrl.getAttribute('href');
        console.log(`Verification URL: ${href}`);
      }
      
    } else {
      // Check for error
      const errorEl = await page.$('#error');
      if (errorEl) {
        const errorText = await errorEl.textContent();
        console.log(`❌ Error displayed: ${errorText}`);
      } else {
        console.log('❌ User code element not found');
        const content = await page.content();
        console.log('Page HTML:', content.substring(0, 2000));
      }
      return;
    }
    
    // Step 4: Wait and watch for polling
    console.log('\n📍 Step 4: Watching polling status...');
    console.log('⏳ The page is now polling. Enter the code at google.com/device to complete.');
    console.log('   Browser will stay open for 5 minutes for you to complete authorization.\n');
    
    // Monitor for success or changes
    let lastStatus = '';
    for (let i = 0; i < 60; i++) { // 5 minutes max
      await sleep(5000);
      
      // Check for success
      const successEl = await page.$('#success');
      if (successEl) {
        const display = await successEl.evaluate(el => getComputedStyle(el).display);
        if (display !== 'none') {
          console.log('\n✅ SUCCESS! Authorization complete!');
          await page.screenshot({ path: 'test-device-4-success.png' });
          console.log('📸 Screenshot: test-device-4-success.png');
          
          // Wait for redirect
          await sleep(3000);
          console.log(`📍 Current URL: ${page.url()}`);
          await page.screenshot({ path: 'test-device-5-token.png' });
          console.log('📸 Screenshot: test-device-5-token.png');
          break;
        }
      }
      
      // Check for error
      const errorEl = await page.$('#error');
      if (errorEl) {
        const display = await errorEl.evaluate(el => getComputedStyle(el).display);
        if (display !== 'none') {
          const errorText = await errorEl.textContent();
          console.log(`\n❌ Error: ${errorText}`);
          break;
        }
      }
      
      // Show polling status
      const pollingEl = await page.$('#polling');
      if (pollingEl && i % 6 === 0) { // Every 30 seconds
        console.log(`⏳ Still waiting... (${Math.floor(i * 5 / 60)} minutes elapsed)`);
      }
    }
    
    console.log('\n✅ Test complete.');
    
  } catch (error) {
    console.error('\n❌ Test error:', error);
    await page.screenshot({ path: 'test-device-error.png' });
  } finally {
    // Keep browser open briefly to see final state
    await sleep(5000);
    await browser.close();
    console.log('👋 Browser closed.');
  }
}

testDeviceFlow().catch(console.error);
