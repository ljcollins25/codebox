/**
 * Automated Device Flow test using Playwright with existing Chromium profile
 * Run with: node test-device-flow.mjs
 */

import { chromium } from 'playwright';
import { spawn, execSync } from 'child_process';
import path from 'path';
import os from 'os';
import fs from 'fs';

const WORKER_URL = 'https://youtube-extractor.ref12cf.workers.dev';
const TEST_VIDEO = 'https://www.youtube.com/watch?v=PSEDW6dEf-s';
const VIDEO_ID = 'PSEDW6dEf-s';

// Global timeout to prevent hanging
const TEST_TIMEOUT_MS = 120000; // 2 minutes
setTimeout(() => {
  console.error('\n⏰ TEST TIMEOUT - exiting after 2 minutes');
  process.exit(1);
}, TEST_TIMEOUT_MS);

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function findChromiumPath() {
  const playwrightPath = path.join(os.homedir(), 'AppData', 'Local', 'ms-playwright', 'chromium-1208', 'chrome-win64', 'chrome.exe');
  return playwrightPath;
}

async function handleGoogleConsentFlow(page, deviceData, pollForToken) {
  let screensHandled = 0;
  const maxScreens = 20;
  
  while (screensHandled < maxScreens) {
    await sleep(2500);
    
    // Check if we got the token
    const tokenResult = await pollForToken();
    if (tokenResult) {
      return tokenResult;
    }
    
    // Get current page text for debugging
    const pageText = await page.evaluate(() => document.body?.innerText || '');
    const preview = pageText.substring(0, 300).replace(/\n/g, ' ');
    console.log(`\n📄 [${screensHandled}] Page: ${preview}...`);
    
    // Check for success page
    if (pageText.includes("You've signed in") || 
        pageText.includes("Success") || 
        pageText.includes("successfully linked") ||
        pageText.includes("You can close this window") ||
        pageText.includes("now connected")) {
      console.log('✅ SUCCESS screen detected!');
      await page.screenshot({ path: `device-flow-success.png` });
      await sleep(2000);
      return await pollForToken();
    }
    
    // If we're on consent page (wants to access), look for Allow button first
    if (pageText.includes('wants to access your Google Account') || pageText.includes('This will allow')) {
      console.log('📝 On consent page, looking for Allow/Continue button...');
      
      // Try Allow button first
      const allowButton = await page.$('button:has-text("Allow"), button:has-text("Continue")');
      if (allowButton && await allowButton.isVisible()) {
        console.log('🖱️  Clicking Allow/Continue button');
        await allowButton.click();
        await page.screenshot({ path: `device-flow-step-${screensHandled}-allow.png` });
        screensHandled++;
        continue;
      }
      
      // Sometimes it's a div role=button
      const divButton = await page.$('div[role="button"]:has-text("Allow"), div[role="button"]:has-text("Continue")');
      if (divButton && await divButton.isVisible()) {
        console.log('🖱️  Clicking Allow/Continue div button');
        await divButton.click();
        await page.screenshot({ path: `device-flow-step-${screensHandled}-allow-div.png` });
        screensHandled++;
        continue;
      }
    }
    
    // Check if we need to select an account (Choose an account page)
    if (pageText.includes('Choose an account')) {
      const accountDivs = await page.$$('div[data-identifier], div[data-email]');
      if (accountDivs.length > 0) {
        const firstAccount = accountDivs[0];
        const visible = await firstAccount.isVisible();
        if (visible) {
          const email = await firstAccount.getAttribute('data-identifier') || await firstAccount.getAttribute('data-email') || 'account';
          console.log(`🖱️  Selecting account: ${email}`);
          await firstAccount.click();
          await page.screenshot({ path: `device-flow-step-${screensHandled}-account.png` });
          screensHandled++;
          continue;
        }
      }
    }
    
    // Look for checkboxes that need to be checked
    const checkboxes = await page.$$('input[type="checkbox"]:not(:checked)');
    for (const cb of checkboxes) {
      const visible = await cb.isVisible().catch(() => false);
      if (visible) {
        console.log('☑️  Checking checkbox');
        await cb.click();
        await sleep(500);
      }
    }
    
    // Try various button patterns (in priority order)
    const buttonPatterns = [
      { selector: 'button:has-text("Allow")', name: 'Allow' },
      { selector: 'button:has-text("Continue")', name: 'Continue' },
      { selector: 'button:has-text("Next")', name: 'Next' },
      { selector: 'button:has-text("Grant access")', name: 'Grant access' },
      { selector: 'button:has-text("Accept")', name: 'Accept' },
      { selector: 'button:has-text("I agree")', name: 'I agree' },
      { selector: 'button:has-text("Agree")', name: 'Agree' },
      { selector: 'button:has-text("Confirm")', name: 'Confirm' },
      { selector: 'button:has-text("Yes")', name: 'Yes' },
      { selector: '#submit_approve_access', name: 'Submit approve' },
      { selector: 'button[type="submit"]', name: 'Submit' },
      { selector: 'div[role="button"]:has-text("Allow")', name: 'Div Allow' },
      { selector: 'div[role="button"]:has-text("Continue")', name: 'Div Continue' },
    ];
    
    let clicked = false;
    for (const {selector, name} of buttonPatterns) {
      try {
        const elements = await page.$$(selector);
        for (const el of elements) {
          const visible = await el.isVisible();
          const enabled = await el.isEnabled().catch(() => true);
          const box = await el.boundingBox();
          
          if (visible && enabled && box) {
            console.log(`🖱️  Clicking: ${name}`);
            await el.click();
            await page.screenshot({ path: `device-flow-step-${screensHandled}.png` });
            clicked = true;
            break;
          }
        }
        if (clicked) break;
      } catch (e) {}
    }
    
    if (!clicked) {
      console.log('⏳ Waiting for page update...');
      await page.screenshot({ path: `device-flow-waiting-${screensHandled}.png` });
    }
    
    screensHandled++;
  }
  
  return null;
}

async function testDeviceFlow() {
  console.log('🚀 Starting Device Flow test...\n');
  
  const chromiumPath = await findChromiumPath();
  console.log(`📍 Using Chromium: ${chromiumPath}`);
  
  const userDataDir = path.join(os.homedir(), 'AppData', 'Local', 'Chromium', 'User Data');
  console.log(`📁 Using existing Chromium profile: ${userDataDir}`);
  
  if (!fs.existsSync(userDataDir)) {
    console.log('❌ No existing Chromium profile found!');
    return;
  }
  
  console.log('🔄 Ensuring no Chromium processes are running...');
  try {
    execSync('taskkill /F /IM chrome.exe 2>nul', { stdio: 'ignore' });
  } catch {}
  await sleep(1000);
  
  const debugPort = 9222;
  console.log(`🔌 Launching Chromium with debug port ${debugPort}...`);
  
  const chromiumProcess = spawn(chromiumPath, [
    `--remote-debugging-port=${debugPort}`,
    `--user-data-dir=${userDataDir}`,
    '--profile-directory=Profile 1',
    '--no-first-run',
    '--no-default-browser-check',
    'about:blank'
  ], {
    detached: true,
    stdio: 'ignore'
  });
  
  await sleep(4000);
  
  let browser;
  let apiToken = null;
  
  try {
    console.log('🔗 Connecting to Chromium via CDP...');
    browser = await chromium.connectOverCDP(`http://localhost:${debugPort}`);
    
    const context = browser.contexts()[0];
    const page = await context.newPage();
    
    // Step 1: Start device flow
    console.log('\n📍 Step 1: Starting device flow...');
    const startResponse = await fetch(`${WORKER_URL}/device/start`, { method: 'POST' });
    const deviceData = await startResponse.json();
    
    if (deviceData.error) {
      throw new Error(`Device start failed: ${deviceData.error}`);
    }
    
    console.log(`✓ Got device code: ${deviceData.user_code}`);
    console.log(`  Verification URL: ${deviceData.verification_url}`);
    console.log(`  Expires in: ${Math.floor(deviceData.expires_in / 60)} minutes`);
    
    const pollForToken = async () => {
      try {
        const pollResponse = await fetch(`${WORKER_URL}/device/poll?code=${encodeURIComponent(deviceData.user_code)}`);
        const pollData = await pollResponse.json();
        
        if (pollData.status === 'success') {
          console.log(`\n🎉 Got API token: ${pollData.token.substring(0, 30)}...`);
          return pollData.token;
        }
      } catch {}
      return null;
    };
    
    // Step 2: Go to Google device page
    console.log('\n📍 Step 2: Navigating to google.com/device...');
    await page.goto('https://www.google.com/device', { waitUntil: 'networkidle' });
    await page.screenshot({ path: 'device-flow-1-google.png' });
    
    // Step 3: Enter the code
    console.log('\n📍 Step 3: Looking for code input...');
    await sleep(1000);
    
    const codeSelectors = ['input[type="text"]', 'input[name="user_code"]', 'input'];
    
    for (const selector of codeSelectors) {
      try {
        const input = await page.$(selector);
        if (input && await input.isVisible()) {
          console.log(`✓ Found input`);
          await input.fill(deviceData.user_code);
          console.log(`✓ Entered code: ${deviceData.user_code}`);
          break;
        }
      } catch {}
    }
    
    await page.screenshot({ path: 'device-flow-2-code-entered.png' });
    
    // Step 4: Click through consent flow
    console.log('\n📍 Step 4: Clicking through consent flow...');
    
    const nextButton = await page.$('button:has-text("Continue"), button:has-text("Next"), button[type="submit"]');
    if (nextButton && await nextButton.isVisible()) {
      console.log('🖱️  Clicking first Next/Continue');
      await nextButton.click();
    }
    
    await sleep(2000);
    await page.screenshot({ path: 'device-flow-3-after-next.png' });
    
    // Handle full consent flow
    apiToken = await handleGoogleConsentFlow(page, deviceData, pollForToken);
    
    // Final poll attempts
    if (!apiToken) {
      console.log('\n📍 Final polling...');
      for (let i = 0; i < 10; i++) {
        apiToken = await pollForToken();
        if (apiToken) break;
        await sleep(3000);
      }
    }
    
    if (!apiToken) {
      await page.screenshot({ path: 'device-flow-final.png' });
      console.log('\n⚠️  Did not get token automatically.');
      console.log(`   Code: ${deviceData.user_code}`);
    }
    
    // Step 5: Test subtitle download
    if (apiToken) {
      console.log('\n📍 Step 5: Testing subtitle download...');
      console.log(`   Video ID: ${VIDEO_ID}`);
      
      const subtitleUrl = `${WORKER_URL}/api/subtitles?v=${VIDEO_ID}`;
      console.log(`   Request URL: ${subtitleUrl}`);
      console.log(`   Token: ${apiToken.substring(0, 30)}...`);
      
      const subtitleResponse = await fetch(subtitleUrl, {
        headers: {
          'Authorization': `Bearer ${apiToken}`
        }
      });
      
      if (subtitleResponse.ok) {
        const subtitles = await subtitleResponse.json();
        console.log('✓ Subtitle response received!');
        console.log(`  Status: ${subtitleResponse.status}`);
        console.log(`  Preview: ${JSON.stringify(subtitles).substring(0, 500)}`);
        
        fs.writeFileSync('subtitles-test.json', JSON.stringify(subtitles, null, 2));
        console.log('💾 Saved to subtitles-test.json');
      } else {
        const error = await subtitleResponse.text();
        console.log(`❌ Subtitle request failed: ${subtitleResponse.status}`);
        console.log(`   Error: ${error.substring(0, 500)}`);
      }
    }
    
    console.log('\n✅ Device flow test complete!');
    
  } catch (error) {
    console.error('\n❌ Test error:', error);
  } finally {
    if (browser) {
      await browser.close();
    }
    try {
      process.kill(-chromiumProcess.pid);
    } catch {}
  }
}

testDeviceFlow().catch(console.error);
