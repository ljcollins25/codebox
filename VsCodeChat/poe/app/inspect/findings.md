# Poe Desktop App - Inspection Findings

## App Identity
- **Name:** Poe
- **Version:** 1.1.39
- **Author:** Quora, Inc.
- **License:** UNLICENSED (proprietary, not open source)
- **Source repo:** `~/ans/poe/poe-electron` (internal Quora path found in build scripts)

## Runtime & Technology Stack

### Electron App
The Poe desktop app is an **Electron** application — a thin native wrapper around the `poe.com` web app.

**Evidence:**
- `LICENSE` file: "Copyright (c) Electron contributors"
- `resources/app.asar` — standard Electron app archive
- Chromium DLLs: `d3dcompiler_47.dll`, `libEGL.dll`, `libGLESv2.dll`, `vk_swiftshader.dll`, `vulkan-1.dll`
- V8 engine files: `v8_context_snapshot.bin`, `snapshot_blob.bin`
- ICU data: `icudtl.dat`
- Media: `ffmpeg.dll`
- Chromium resources: `chrome_100_percent.pak`, `chrome_200_percent.pak`, `resources.pak`
- Squirrel updater: `squirrel.exe` (Electron's Windows auto-updater)

### Bundler
- **esbuild** — output is ESM (`.mjs` files), esbuild found in node_modules
- Two bundles: `main.mjs` (15KB, main process) and `preload.mjs` (1KB, preload bridge)

### Key Dependencies (from node_modules)
| Package | Purpose |
|---------|---------|
| electron-squirrel-startup | Squirrel installer/updater integration |
| electron-store | Persistent settings (wraps `conf`) |
| electron-log | Logging |
| electron-context-menu | Right-click context menus |
| electron-dl | File download (e.g., save image) |
| systeminformation | System/OS info for User-Agent string |
| @electron-forge | Build tooling |

## Architecture

### How It Works
1. The main process (`main.mjs`) creates a `BrowserWindow`
2. It loads `https://poe.com` (or `POE_APP_BASE_URL` env override) in the window
3. The preload script (`preload.mjs`) bridges Electron APIs to the renderer via `contextBridge`
4. The web app running in the renderer calls `window.electronAPI.*` for native features

### Main Process Features (from main.mjs analysis)
- **Window management** — multiple windows, restore bounds, zoom controls, float-on-top
- **Custom protocol** — `poe-app://` deep links
- **Auto-updater** — Squirrel-based, checks hourly, updater URLs:
  - Mac: `https://updater.poe.com/{platform}_{arch}/{version}`
  - Windows: `https://desktop-app.poecdn.net/updates/{platform}_{arch}`
- **System tray** — Windows tray icon with context menu
- **Global shortcut** — `CmdOrCtrl+/` (configurable), stored in electron-store
- **Login item** — Launch on login with "keep in background" option
- **macOS Dock** — Auto-add to Dock on first run
- **Push notifications** — via Electron Notification API
- **Badge count** — unread message count
- **Custom User-Agent** — format: `Poe {version} rv:0 env:{prod|dev} ({model} {arch}; {distro} Version {release} (Build {build}); en_US)`
- **Context menu** — Save Image As, copy link/image/video, inspect element (dev mode)
- **Navigation guards** — external links open in system browser, internal links stay in-app
- **Error recovery** — retry on network failure with exponential backoff, offline/error pages
- **Dark/light theme** — Windows titlebar overlay synced with `nativeTheme`

### Preload API (`window.electronAPI`)
| Method | Direction | Purpose |
|--------|-----------|---------|
| `getInfo()` | sync | Get app version + platform |
| `updatePreferences(prefs)` | send | Update global shortcut settings |
| `loadPreferences()` | invoke | Load current preferences |
| `canGoBack()` | sync | Check navigation history |
| `canGoForward()` | sync | Check navigation history |
| `popupMenu()` | send | Show app menu |
| `updateWindowTheme(theme)` | send | Sync theme to titlebar |
| `onGlobalShortcutTriggered(cb)` | listen | Global shortcut pressed |
| `reloadElectronApp()` | send | Reload/restart app |
| `setBadgeCount(count)` | send | Set badge + show notification |
| `showPushNotification(data, opts)` | send | Show native push notification |
| `onNavigateToUrl(cb)` | listen | Navigate from notification click |

### Environment Variables
| Variable | Purpose |
|----------|---------|
| `POE_APP_BASE_URL` | Override base URL (default: `https://poe.com`) |
| `POE_APP_OVERRIDE_UPDATER_URL` | Override auto-update feed URL |
| `POE_APP_DEBUG` | Enable debug mode (shows DevTools in context menu) |

### Electron Store Keys
| Key | Purpose |
|-----|---------|
| `winBounds` | Window position/size |
| `globalShortcut.accelerator` | Shortcut key combo |
| `globalShortcut.asked` | Whether shortcut prompt shown |
| `globalShortcut.enabled` | Shortcut enabled |
| `keepInDock.asked` | macOS dock prompt shown |
| `loginItem.asked` | Login item prompt shown |
| `loginItem.keepInBackground` | Background launch mode |

## Build & Distribution
- **Platforms:** macOS (zip + dmg), Windows (Squirrel installer)
- **Build scripts:** `scripts/build_mac.sh`, `scripts/upload_mac_to_aws.sh`, `scripts/upload_windows_to_aws.sh`
- **Distribution:** AWS-hosted updates via `poecdn.net`

## Static Assets
- Tray icons: `tray_colored.ico`, `tray_white.ico` (~225KB each)
- Notification sound: `notifSound.mp3` (5KB)
- Error pages: `offline.html`, `unknown_error.html` with `Caution.svg`, `Poe_Offline.svg`
