# Poe Desktop App Specification

## Overview
The Poe desktop app is a **proprietary Electron application** by **Quora, Inc.** that serves as a thin native wrapper around the `poe.com` web application. It is **not open source** (UNLICENSED). The internal source repo is `~/ans/poe/poe-electron`.

**Current version analyzed:** 1.1.39  
**Install path:** `C:\Users\lancec\AppData\Local\Poe\app-1.1.39`

## Technology Stack

| Layer | Technology |
|-------|-----------|
| Runtime | Electron (Chromium + Node.js) |
| Language | JavaScript/TypeScript (bundled to ESM `.mjs`) |
| Bundler | esbuild |
| Installer/Updater | Squirrel |
| Build tooling | @electron-forge |
| Package format | app.asar |

## Architecture

The app is a **wrapper** — it loads `https://poe.com` in a `BrowserWindow` and adds native desktop features:

- **Window management** — multiple windows, bounds persistence, zoom, float-on-top
- **Custom protocol** — `poe-app://` deep link handler
- **Auto-update** — Squirrel-based, hourly checks against `poecdn.net` (Windows) / `updater.poe.com` (Mac)
- **System tray** — Windows tray icon with launch-on-login controls
- **Global shortcut** — configurable (default `CmdOrCtrl+/`)
- **Notifications** — native push notifications with badge count
- **Context menu** — image download, copy, inspect (dev mode)
- **Theme sync** — Windows titlebar follows dark/light mode
- **Error recovery** — exponential backoff retry, offline error pages

## IPC Bridge

The preload script exposes `window.electronAPI` to the poe.com renderer:

| API | Type | Purpose |
|-----|------|---------|
| `getInfo()` | sync | App version + platform |
| `loadPreferences()` / `updatePreferences()` | invoke/send | Global shortcut settings |
| `canGoBack()` / `canGoForward()` | sync | Navigation state |
| `popupMenu()` | send | Show native menu |
| `updateWindowTheme(theme)` | send | Sync titlebar theme |
| `reloadElectronApp()` | send | Reload app |
| `setBadgeCount(n)` | send | Badge + notification |
| `showPushNotification(data, opts)` | send | Native notifications |
| `onGlobalShortcutTriggered(cb)` | event | Shortcut listener |
| `onNavigateToUrl(cb)` | event | Navigation from notifications |

## Configuration

### Environment Variables
| Variable | Purpose | Default |
|----------|---------|---------|
| `POE_APP_BASE_URL` | Base URL loaded in window | `https://poe.com` |
| `POE_APP_OVERRIDE_UPDATER_URL` | Custom update feed URL | platform-specific |
| `POE_APP_DEBUG` | Enable DevTools/debug | `false` |

### Persistent Storage (electron-store)
Window bounds, global shortcut config, login item preferences, dock preferences.

## Distribution
- **Windows:** Squirrel installer, updates from `desktop-app.poecdn.net`
- **macOS:** zip + dmg, updates from `updater.poe.com`
- **Build/upload scripts** hosted in the asar alongside app code

## Analysis Files
- Detailed findings: [inspect/findings.md](inspect/findings.md)
- Extracted source: [inspect/asar-extract/](inspect/asar-extract/)
- Resume context: [analysis-resume.md](analysis-resume.md)
