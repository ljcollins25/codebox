// publish-bots.mjs
// Creates or updates Poe script bots by driving the poe.com web UI via Playwright CDP.
//
// Usage:
//   node publish-bots.mjs <folder-or-yml-path>
//
// Prerequisites:
//   - Chromium running with --remote-debugging-port=9222
//   - User logged into poe.com in that browser
//   - npm packages: playwright, js-yaml
//
// See spec.md for full details.

import { chromium } from 'playwright';
import { readFileSync, readdirSync, existsSync } from 'fs';
import { join, basename, extname, dirname, resolve } from 'path';
import yaml from 'js-yaml';

const CDP_URL = 'http://localhost:9222';
const TIMEOUT = 20_000;
const NAV_TIMEOUT = 30_000;

// ── Helpers ──

function withTimeout(promise, label = 'operation', ms = TIMEOUT) {
  return Promise.race([
    promise,
    new Promise((_, reject) =>
      setTimeout(() => reject(new Error(`Timeout after ${ms}ms: ${label}`)), ms)
    ),
  ]);
}

function log(msg) {
  console.log(`[${new Date().toISOString().substring(11, 19)}] ${msg}`);
}

// Access value mapping: YAML value → <select> option value
const ACCESS_MAP = {
  everyone: 'public',
  link: 'unlisted',
  private: 'private',
};

// ── Discover bot file pairs ──

function discoverBots(inputPath) {
  const resolved = resolve(inputPath);
  let dir, files;

  if (resolved.endsWith('.yml') || resolved.endsWith('.yaml')) {
    // Single file specified
    dir = dirname(resolved);
    const base = basename(resolved, extname(resolved));
    files = [base];
  } else {
    // Folder specified
    dir = resolved;
    const allFiles = readdirSync(dir);
    const ymlFiles = allFiles.filter(f => f.endsWith('.yml') || f.endsWith('.yaml'));
    files = ymlFiles.map(f => basename(f, extname(f)));
  }

  const bots = [];
  for (const base of files) {
    const ymlPath = existsSync(join(dir, `${base}.yml`))
      ? join(dir, `${base}.yml`)
      : join(dir, `${base}.yaml`);
    const pyPath = join(dir, `${base}.py`);

    if (!existsSync(ymlPath)) {
      console.warn(`  SKIP: No YAML file for "${base}"`);
      continue;
    }
    if (!existsSync(pyPath)) {
      console.warn(`  SKIP: No .py file for "${base}" (need ${pyPath})`);
      continue;
    }

    const meta = yaml.load(readFileSync(ymlPath, 'utf8'));
    const code = readFileSync(pyPath, 'utf8');

    if (!meta.name) {
      console.warn(`  SKIP: "${base}.yml" missing required "name" field`);
      continue;
    }

    bots.push({ base, meta, code, ymlPath, pyPath });
  }
  return bots;
}

// ── Check if bot exists ──

async function botExists(page, name) {
  log(`Checking if bot "${name}" exists...`);
  const url = `https://poe.com/edit_bot?bot=${name}`;
  try {
    const resp = await withTimeout(
      page.goto(url, { waitUntil: 'domcontentloaded', timeout: NAV_TIMEOUT }),
      'goto edit_bot',
      NAV_TIMEOUT
    );
    // Wait for form to render
    await page.waitForTimeout(2000);

    const title = await page.title();
    const currentUrl = page.url();

    // If we landed on the edit page, the bot exists
    if (title.includes('Edit') && currentUrl.includes('edit_bot')) {
      log(`Bot "${name}" exists (edit page loaded)`);
      return true;
    }

    log(`Bot "${name}" does not exist (title: "${title}", url: ${currentUrl})`);
    return false;
  } catch (err) {
    log(`Bot "${name}" existence check failed: ${err.message}`);
    return false;
  }
}

// ── Fill the bot form ──

async function fillBotForm(page, meta, code, isCreate) {
  const action = isCreate ? 'Creating' : 'Updating';
  log(`${action} bot "${meta.name}"...`);

  // If creating, select "Script bot" type
  if (isCreate) {
    log('  Setting bot type to "script"...');
    const typeSelect = page.locator('select').filter({ hasText: /Script bot/i }).first();
    await withTimeout(typeSelect.selectOption('script'), 'select script type');
    // Wait for form to re-render with script-specific fields
    await page.waitForTimeout(1500);
  }

  // Fill handle/name
  log(`  Setting handle to "${meta.name}"...`);
  const handleInput = page.locator('input[name="handle"]');
  await withTimeout(handleInput.fill(meta.name), 'fill handle');

  // Fill description
  if (meta.description !== undefined) {
    log(`  Setting description (${meta.description.length} chars)...`);
    const descTextarea = page.locator('textarea[name="description"]');
    await withTimeout(descTextarea.fill(meta.description), 'fill description');
  }

  // Fill Python code
  log(`  Setting Python code (${code.length} chars)...`);
  const promptTextarea = page.locator('textarea[name="prompt"]');
  await withTimeout(promptTextarea.fill(code), 'fill prompt/code');

  // Set access level
  const access = meta.access || 'private';
  const accessValue = ACCESS_MAP[access] || 'private';
  log(`  Setting access to "${access}" (select value: "${accessValue}")...`);
  const accessSelect = page.locator('select[name="botIsPublicOption"]');
  await withTimeout(accessSelect.selectOption(accessValue), 'select access');

  // Set allow_remix — uses a custom switch control; click the parent label to toggle
  if (meta.allow_remix !== undefined) {
    const remixCheckbox = page.locator('input[name="promptIsPublic"]');
    const isChecked = await remixCheckbox.isChecked();
    if (meta.allow_remix !== isChecked) {
      log(`  ${meta.allow_remix ? 'Enabling' : 'Disabling'} allow remix...`);
      // The checkbox is hidden behind a styled switch — click its label/parent instead
      const remixLabel = page.locator('label:has(input[name="promptIsPublic"])');
      if (await remixLabel.count() > 0) {
        await withTimeout(remixLabel.click(), 'click allow_remix label');
      } else {
        // Fallback: force-click the checkbox via JS
        await withTimeout(
          page.evaluate(() => {
            const cb = document.querySelector('input[name="promptIsPublic"]');
            if (cb) { cb.click(); }
          }),
          'js click allow_remix'
        );
      }
    }
  }

  // Set daily message limit (toggle + value)
  if (meta.daily_message_limit !== undefined) {
    const limitCheckbox = page.locator('input[name="hasCustomMessageLimit"]');
    const limitEnabled = await limitCheckbox.isChecked();
    const wantLimit = meta.daily_message_limit !== null;

    if (wantLimit !== limitEnabled) {
      log(`  ${wantLimit ? 'Enabling' : 'Disabling'} daily message limit...`);
      const limitLabel = page.locator('label:has(input[name="hasCustomMessageLimit"])');
      if (await limitLabel.count() > 0) {
        await withTimeout(limitLabel.click(), 'click daily_message_limit label');
      } else {
        await withTimeout(
          page.evaluate(() => {
            const cb = document.querySelector('input[name="hasCustomMessageLimit"]');
            if (cb) { cb.click(); }
          }),
          'js click daily_message_limit'
        );
      }
      // Wait for the limit input to appear/disappear
      await page.waitForTimeout(1000);
    }

    if (wantLimit) {
      log(`  Setting daily message limit to ${meta.daily_message_limit}...`);
      const limitInput = page.locator('input[name="customMessageLimit"]');
      await withTimeout(limitInput.fill(String(meta.daily_message_limit)), 'fill daily_message_limit');
    }
  }

  // Set message price
  if (meta.message_price !== undefined && meta.message_price !== null) {
    log(`  Setting message price to $${meta.message_price} per 1000 messages...`);
    const priceInput = page.locator('input[name="messagePrice"]');
    await withTimeout(priceInput.fill(String(meta.message_price)), 'fill message_price');
  }
}

// ── Submit the form ──

async function submitForm(page, meta) {
  log('  Clicking Publish...');

  // Find the primary submit button with "Publish" or "Create" text
  const publishBtn = page.locator('button[type="submit"]').filter({ hasText: /Publish|Create/i }).first();

  // Listen for navigation or response
  const navPromise = page.waitForURL(url => !url.includes('edit_bot') && !url.includes('create_bot'), {
    timeout: NAV_TIMEOUT,
  }).catch(() => null);

  await withTimeout(publishBtn.click(), 'click publish');

  // Wait a beat, then check for errors or success
  await page.waitForTimeout(3000);

  // Check for error messages on the page
  const errorText = await page.evaluate(() => {
    // Look for common error indicators
    const errorEls = document.querySelectorAll('[class*="error"], [class*="Error"], [role="alert"]');
    for (const el of errorEls) {
      const text = el.textContent?.trim();
      if (text && el.getBoundingClientRect().width > 0) return text;
    }
    return null;
  });

  if (errorText) {
    log(`  ❌ Error: ${errorText}`);
    return false;
  }

  // Check if we got redirected (success) or still on edit page
  const currentUrl = page.url();
  if (currentUrl.includes('edit_bot') || currentUrl.includes('create_bot')) {
    // Might still be on the form — could be success with no redirect, or an error
    // Check if there's a success toast
    const toastText = await page.evaluate(() => {
      const toasts = document.querySelectorAll('[class*="toast"], [class*="Toast"], [class*="snackbar"], [class*="Snackbar"]');
      for (const el of toasts) {
        const text = el.textContent?.trim();
        if (text && el.getBoundingClientRect().width > 0) return text;
      }
      return null;
    });
    if (toastText) {
      log(`  ℹ️  Toast: ${toastText}`);
    }
    log(`  ✅ Submitted (still on form page — may be normal for updates)`);
  } else {
    log(`  ✅ Published! Redirected to: ${currentUrl}`);
  }

  return true;
}

// ── Process one bot ──

async function processBot(page, bot) {
  const { meta, code, base } = bot;
  log(`\n${'═'.repeat(60)}`);
  log(`Processing: ${base} (handle: ${meta.name})`);
  log(`${'═'.repeat(60)}`);

  const exists = await botExists(page, meta.name);

  if (!exists) {
    // Create flow
    log('Navigating to create_bot page...');
    await withTimeout(
      page.goto('https://poe.com/create_bot', { waitUntil: 'domcontentloaded', timeout: NAV_TIMEOUT }),
      'goto create_bot',
      NAV_TIMEOUT
    );
    await page.waitForTimeout(2000);
  }
  // If exists, we're already on the edit page from the existence check

  await fillBotForm(page, meta, code, !exists);
  const success = await submitForm(page, meta);

  return { name: meta.name, base, action: exists ? 'update' : 'create', success };
}

// ── Main ──

async function main() {
  const inputPath = process.argv[2];
  if (!inputPath) {
    console.error('Usage: node publish-bots.mjs <folder-or-yml-path>');
    process.exit(1);
  }

  // Discover bots
  log(`Discovering bots from: ${inputPath}`);
  const bots = discoverBots(inputPath);
  if (bots.length === 0) {
    console.error('No valid bot pairs found. Need matching .yml + .py files.');
    process.exit(1);
  }
  log(`Found ${bots.length} bot(s): ${bots.map(b => b.meta.name).join(', ')}\n`);

  // Connect to browser
  log(`Connecting to Chromium at ${CDP_URL}...`);
  const browser = await withTimeout(
    chromium.connectOverCDP(CDP_URL, { timeout: TIMEOUT }),
    'CDP connect'
  );
  log(`Connected. ${browser.contexts().length} context(s)`);

  const context = browser.contexts()[0];
  let page = context.pages().find(p => p.url().includes('poe.com'));
  if (!page) {
    page = await context.newPage();
  }

  // Process each bot
  const results = [];
  for (const bot of bots) {
    try {
      const result = await processBot(page, bot);
      results.push(result);
    } catch (err) {
      log(`❌ Failed to process ${bot.base}: ${err.message}`);
      results.push({ name: bot.meta.name, base: bot.base, action: 'unknown', success: false, error: err.message });
    }
  }

  // Summary
  log(`\n${'═'.repeat(60)}`);
  log('SUMMARY');
  log(`${'═'.repeat(60)}`);
  for (const r of results) {
    const icon = r.success ? '✅' : '❌';
    log(`  ${icon} ${r.name} — ${r.action}${r.error ? ` (${r.error})` : ''}`);
  }

  const ok = results.filter(r => r.success).length;
  const fail = results.filter(r => !r.success).length;
  log(`\n${ok} succeeded, ${fail} failed`);
}

main().catch(err => {
  console.error('Fatal error:', err);
  process.exit(1);
});
