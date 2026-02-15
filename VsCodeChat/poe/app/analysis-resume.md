# Poe Desktop App - Analysis Resume

## Original Prompt
> Lets analyze the poe app. We will start with desktop app and move to web app later. Desktop app is at: C:\Users\lancec\AppData\Local\Poe\app-1.1.39. Store analysis finding in inspect folder. What type of app is this? I'm curious what runtime it uses or what was used to implement the app. Not sure if its open source or not. Also, keep an analysis-resume.md file for resuming analysis. Also, update spec.md with current knowledge as it is discovered during analysis. This prompt should be captured in resume file.

## Analysis Status

### Completed
- [x] Directory structure exploration
- [x] Identified runtime: **Electron** (Chromium + Node.js)
- [x] Extracted `app.asar` to `inspect/asar-extract/`
- [x] Analyzed `main.mjs` (main process) - minified/bundled, single file ~15KB
- [x] Analyzed `preload.mjs` (preload bridge) - exposes `electronAPI` to renderer
- [x] Identified build scripts (Mac builds, AWS uploads)
- [x] Identified dependencies from `node_modules`
- [x] Identified author: **Quora, Inc.** / license: **UNLICENSED** (proprietary)
- [x] Identified source repo path from build scripts: `~/ans/poe/poe-electron`

### Pending
- [ ] Web app analysis (deferred for later)
- [ ] Deeper analysis of Poe web app integration (renderer is poe.com loaded in BrowserWindow)
- [ ] Network traffic / API analysis
- [ ] IPC message catalog deep dive

## Key Findings Summary

| Aspect | Detail |
|--------|--------|
| App Type | Electron desktop app (thin wrapper around poe.com) |
| Version | 1.1.39 |
| Author | Quora, Inc. |
| License | UNLICENSED (proprietary, closed source) |
| Runtime | Electron (Chromium + Node.js) |
| Bundler | esbuild (ESM output as `.mjs`) |
| Updater | Squirrel (auto-updater) |
| Source repo | `~/ans/poe/poe-electron` (internal Quora repo) |
| Base URL | `https://poe.com` (configurable via `POE_APP_BASE_URL` env) |
| Protocol | `poe-app://` custom protocol handler |

## File Locations
- App install: `C:\Users\lancec\AppData\Local\Poe\app-1.1.39`
- Extracted asar: `poe/app/inspect/asar-extract/`
- Findings: `poe/app/inspect/`
- Spec: `poe/app/spec.md`
