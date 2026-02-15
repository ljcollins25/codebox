// capture-bot-edit.mjs
// Connects to existing Chromium via CDP, captures network traffic on Poe bot edit page,
// and interactively explores the UI.
//
// Usage: node capture-bot-edit.mjs [bot-handle]

import { chromium } from 'playwright';
import { writeFileSync, mkdirSync, existsSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const outputDir = join(__dirname, 'inspect', 'network-captures');

const botHandle = process.argv[2] || 'TestBotASDNSAD4FT';
const editUrl = `https://poe.com/edit_bot?bot=${botHandle}`;
const CDP_URL = 'http://localhost:9222';
const TIMEOUT = 20_000; // 20 seconds max for any single operation

function withTimeout(promise, label = 'operation') {
  return Promise.race([
    promise,
    new Promise((_, reject) =>
      setTimeout(() => reject(new Error(`Timeout after ${TIMEOUT}ms: ${label}`)), TIMEOUT)
    ),
  ]);
}

if (!existsSync(outputDir)) {
  mkdirSync(outputDir, { recursive: true });
}

// ── Network capture ──
const captured = [];
let reqCounter = 0;

function hookNetwork(page) {
  page.on('request', req => {
    const url = req.url();
    const entry = {
      id: ++reqCounter,
      ts: new Date().toISOString(),
      method: req.method(),
      url,
      headers: req.headers(),
      postData: null,
    };
    try { entry.postData = req.postData(); } catch {}
    captured.push(entry);
    if (url.includes('/api/') || url.includes('gql') || url.includes('graphql') || url.includes('bot'))
      console.log(`  >> [${entry.id}] ${entry.method} ${url.substring(0, 140)}`);
  });

  page.on('response', async res => {
    const url = res.url();
    const entry = captured.find(e => e.url === url && !e.status);
    if (!entry) return;
    entry.status = res.status();
    entry.responseHeaders = res.headers();
    try {
      const ct = res.headers()['content-type'] || '';
      if (ct.includes('json') || ct.includes('text')) {
        entry.responseBody = await withTimeout(res.text(), `response.text ${url.substring(0, 80)}`);
        try { entry.responseParsed = JSON.parse(entry.responseBody); } catch {}
      }
    } catch (e) { entry.responseError = e.message; }
    if (url.includes('/api/') || url.includes('gql') || url.includes('graphql') || url.includes('bot'))
      console.log(`  << [${entry.id}] ${entry.status} ${url.substring(0, 140)}`);
  });
}

function saveCaptures(tag = '') {
  const suffix = tag ? `-${tag}` : '';
  const file = join(outputDir, `traffic-${botHandle}${suffix}.json`);
  const relevant = captured.filter(e =>
    e.url.includes('/api/') || e.url.includes('gql') || e.url.includes('graphql') ||
    e.url.includes('bot') || e.url.includes('settings') || e.url.includes('create') || e.url.includes('edit')
  );
  writeFileSync(file, JSON.stringify(relevant, null, 2));
  console.log(`\nSaved ${relevant.length} relevant request(s) (of ${captured.length} total) to ${file}`);
  return relevant;
}

// ── Main ──
async function main() {
  console.log(`Connecting to Chromium at ${CDP_URL}...`);
  const browser = await withTimeout(chromium.connectOverCDP(CDP_URL, { timeout: TIMEOUT }), 'CDP connect');
  console.log(`Connected. ${browser.contexts().length} context(s)\n`);

  const context = browser.contexts()[0];

  // Use existing tab or open new one
  let page = context.pages().find(p => p.url().includes('poe.com'));
  if (!page) {
    page = await context.newPage();
  }
  hookNetwork(page);

  // Step 1: Navigate to bot edit page
  console.log(`=== Step 1: Navigate to ${editUrl} ===`);
  await withTimeout(page.goto(editUrl, { waitUntil: 'domcontentloaded', timeout: TIMEOUT }), 'page.goto');
  // Give extra time for JS to render after DOMContentLoaded
  await page.waitForTimeout(2000);
  console.log(`Page title: "${await page.title()}"`);  
  saveCaptures('1-page-load');

  // Step 2: Explore the page structure
  console.log(`\n=== Step 2: Analyzing page structure ===`);

  const formElements = await withTimeout(page.evaluate(() => {
    const elements = [];
    const inputs = document.querySelectorAll('input, textarea, select, button, [role="button"], [contenteditable]');
    inputs.forEach(el => {
      const rect = el.getBoundingClientRect();
      elements.push({
        tag: el.tagName.toLowerCase(),
        type: el.type || '',
        name: el.name || '',
        id: el.id || '',
        class: el.className?.toString()?.substring(0, 100) || '',
        placeholder: el.placeholder || '',
        value: el.value?.substring(0, 200) || '',
        text: el.textContent?.trim()?.substring(0, 100) || '',
        ariaLabel: el.getAttribute('aria-label') || '',
        visible: rect.width > 0 && rect.height > 0,
        role: el.getAttribute('role') || '',
      });
    });
    return elements;
  }), 'evaluate:formElements');

  console.log(`Found ${formElements.length} form elements:`);
  const visibleElements = formElements.filter(e => e.visible);
  visibleElements.forEach(el => {
    const label = el.ariaLabel || el.placeholder || el.name || el.text?.substring(0, 40) || el.id || '(unnamed)';
    console.log(`  [${el.tag}${el.type ? ':' + el.type : ''}] ${label} ${el.value ? '= "' + el.value.substring(0, 60) + '"' : ''}`);
  });

  // Get all visible text sections / labels
  const sections = await withTimeout(page.evaluate(() => {
    const items = [];
    document.querySelectorAll('h1, h2, h3, h4, label, [class*="label"], [class*="Label"], [class*="heading"], [class*="Heading"], [class*="section"], [class*="Section"]').forEach(el => {
      const rect = el.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0) {
        items.push({
          tag: el.tagName.toLowerCase(),
          text: el.textContent?.trim()?.substring(0, 150) || '',
          class: el.className?.toString()?.substring(0, 80) || '',
        });
      }
    });
    return items;
  }), 'evaluate:sections');

  const pageStructure = {
    url: page.url(),
    title: await page.title(),
    formElements,
    sections,
  };

  console.log(`\nPage sections/labels:`);
  sections.forEach(s => console.log(`  <${s.tag}> ${s.text}`));

  writeFileSync(join(outputDir, `page-structure-${botHandle}.json`), JSON.stringify(pageStructure, null, 2));
  console.log(`\nSaved page structure`);

  // Step 3: Take a screenshot
  const screenshotPath = join(outputDir, `bot-edit-${botHandle}.png`);
  await withTimeout(page.screenshot({ path: screenshotPath, fullPage: true, timeout: TIMEOUT }), 'screenshot');
  console.log(`Screenshot saved to ${screenshotPath}`);

  // Step 4: Search for API patterns in page source
  console.log(`\n=== Step 3: Searching for API patterns in page ===`);
  const apiPatterns = await withTimeout(page.evaluate(() => {
    const patterns = [];
    const pageHtml = document.documentElement.innerHTML;
    const regexes = [
      /fetch\s*\(\s*["']([^"']+)["']/g,
      /["'](\/api\/[^"']+)["']/g,
      /["'](https?:\/\/[^"']*(?:api|graphql|gql|mutation|query)[^"']*)/gi,
      /mutation\s+(\w+)/g,
      /query\s+(\w+)/g,
    ];
    for (const regex of regexes) {
      let match;
      while ((match = regex.exec(pageHtml)) !== null) {
        patterns.push({ pattern: match[0].substring(0, 200), captured: match[1]?.substring(0, 200) });
      }
    }
    return patterns;
  }), 'evaluate:apiPatterns');

  console.log(`Found ${apiPatterns.length} API-like patterns:`);
  const seen = new Set();
  apiPatterns.filter(p => {
    const key = p.captured || p.pattern;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  }).forEach(p => console.log(`  ${p.captured || p.pattern}`));

  writeFileSync(join(outputDir, `api-patterns-${botHandle}.json`), JSON.stringify(apiPatterns, null, 2));

  // Step 5: Save the full HTML
  const html = await withTimeout(page.content(), 'page.content');
  writeFileSync(join(outputDir, `bot-edit-page-${botHandle}.html`), html);
  console.log(`Saved page HTML`);

  // Final save
  saveCaptures('final');

  console.log(`\n=== Analysis complete ===`);
  console.log(`All outputs saved to: ${outputDir}`);
}

main().catch(err => {
  console.error('Fatal error:', err);
  process.exit(1);
});
