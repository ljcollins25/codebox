/**
 * Automated OAuth test script using Playwright
 * Run with: node test-oauth.mjs
 */

import { chromium } from 'playwright';

const WORKER_URL = 'https://youtube-extractor.ref12cf.workers.dev';
const TEST_EMAIL = 'ljcollins25@gmail.com';

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function testOAuth() {
  console.log('🚀 Starting OAuth test...\n');
  
  const browser = await chromium.launch({
    headless: false, // Show the browser
    slowMo: 500, // Slow down for visibility
  });
  
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  });
  
  const page = await context.newPage();
  
  // Enable console logging from the page
  page.on('console', msg => {
    const type = msg.type();
    if (type === 'error') {
      console.log(`❌ PAGE ERROR: ${msg.text()}`);
    } else if (type === 'warning') {
      console.log(`⚠️  PAGE WARN: ${msg.text()}`);
    } else {
      console.log(`📄 PAGE: ${msg.text()}`);
    }
  });
  
  // Log network errors
  page.on('requestfailed', request => {
    console.log(`❌ REQUEST FAILED: ${request.url()} - ${request.failure()?.errorText}`);
  });
  
  // Log responses
  page.on('response', response => {
    const status = response.status();
    const url = response.url();
    if (status >= 400) {
      console.log(`❌ ${status} ${url}`);
    } else if (url.includes('youtube-extractor') || url.includes('google.com')) {
      console.log(`✓ ${status} ${url.substring(0, 100)}...`);
    }
  });
  
  try {
    // Step 1: Go to OAuth start page
    console.log('\n📍 Step 1: Navigating to /oauth/start...');
    await page.goto(`${WORKER_URL}/oauth/start`, { waitUntil: 'networkidle' });
    
    // Wait for service worker registration
    console.log('⏳ Waiting for service worker...');
    await sleep(3000);
    
    // Take screenshot
    await page.screenshot({ path: 'test-oauth-1-start.png' });
    console.log('📸 Screenshot saved: test-oauth-1-start.png');
    
    // Check current URL
    console.log(`📍 Current URL: ${page.url()}`);
    
    // Step 2: Wait for redirect to Google or proxied Google
    console.log('\n📍 Step 2: Waiting for Google sign-in page...');
    await page.waitForTimeout(5000);
    
    await page.screenshot({ path: 'test-oauth-2-google.png' });
    console.log('📸 Screenshot saved: test-oauth-2-google.png');
    console.log(`📍 Current URL: ${page.url()}`);
    
    // Check page content
    const pageContent = await page.content();
    if (pageContent.includes('Sign in') || pageContent.includes('Google')) {
      console.log('✓ Google sign-in page detected');
    } else if (pageContent.includes('error') || pageContent.includes('Error') || pageContent.includes('Proxy error')) {
      console.log('❌ Error page detected');
      console.log('Page content preview:', pageContent.substring(0, 1000));
    }
    
    // Step 3: Try to find and fill email field
    console.log('\n📍 Step 3: Looking for email field...');
    
    const emailSelectors = [
      'input[type="email"]',
      'input[name="identifier"]',
      '#identifierId',
      'input[autocomplete="username"]',
    ];
    
    for (const selector of emailSelectors) {
      try {
        const emailInput = await page.$(selector);
        if (emailInput) {
          console.log(`✓ Found email field: ${selector}`);
          await emailInput.fill(TEST_EMAIL);
          console.log(`✓ Filled email: ${TEST_EMAIL}`);
          
          await page.screenshot({ path: 'test-oauth-3-email.png' });
          console.log('📸 Screenshot saved: test-oauth-3-email.png');
          
          // Try to click Next button
          const nextSelectors = [
            '#identifierNext',
            'button[type="submit"]',
            'button:has-text("Next")',
            'span:has-text("Next")',
          ];
          
          for (const nextSel of nextSelectors) {
            try {
              const nextBtn = await page.$(nextSel);
              if (nextBtn) {
                console.log(`✓ Found Next button: ${nextSel}`);
                await nextBtn.click();
                console.log('✓ Clicked Next');
                break;
              }
            } catch {}
          }
          
          break;
        }
      } catch {}
    }
    
    // Wait and screenshot
    await sleep(5000);
    await page.screenshot({ path: 'test-oauth-4-after-email.png' });
    console.log('📸 Screenshot saved: test-oauth-4-after-email.png');
    console.log(`📍 Current URL: ${page.url()}`);
    
    // Step 4: Look for password field or error
    console.log('\n📍 Step 4: Looking for password field or errors...');
    
    const pageContent2 = await page.content();
    if (pageContent2.includes('Couldn\'t find your Google Account') || 
        pageContent2.includes('couldn\'t find') ||
        pageContent2.includes('Try again')) {
      console.log('⚠️  Google couldn\'t find the account - may need verification');
    }
    
    const passwordSelectors = [
      'input[type="password"]',
      'input[name="Passwd"]', 
      '#password input',
    ];
    
    for (const selector of passwordSelectors) {
      try {
        const pwdInput = await page.$(selector);
        if (pwdInput) {
          console.log(`✓ Found password field: ${selector}`);
          console.log('⏳ Waiting for user to enter password manually...');
          break;
        }
      } catch {}
    }
    
    // Keep browser open for manual inspection
    console.log('\n✅ Test complete. Browser will stay open for 120 seconds for inspection...');
    console.log('   Check the screenshots and browser window.');
    console.log('   Press Ctrl+C to exit early.\n');
    
    await sleep(120000);
    
  } catch (error) {
    console.error('\n❌ Test error:', error);
    await page.screenshot({ path: 'test-oauth-error.png' });
    console.log('📸 Error screenshot saved: test-oauth-error.png');
  } finally {
    await browser.close();
    console.log('\n👋 Browser closed.');
  }
}

testOAuth().catch(console.error);
