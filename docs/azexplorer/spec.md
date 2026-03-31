# Azure Storage Explorer - Web SPA

## Overview

A single-page web application that replicates core Azure Storage Explorer functionality in the browser. It enables users to browse, download, and manage files across multiple Azure Blob Storage accounts using either Azure AD (OAuth) credentials or SAS URLs.

## Features

### Authentication

- **Azure AD (OAuth 2.0)** via MSAL.js — interactive login with popup/redirect flow.
- **SAS URL** — supports account-level, container-level, and folder-level SAS tokens.
- **Non-standard URLs** — SAS URLs may point to intermediate proxy sites rather than `*.blob.core.windows.net`; the app must not validate or reject URLs based on hostname.
- **Multiple accounts** — users can add multiple credentials (Azure AD or SAS) and browse them simultaneously in a sidebar tree, similar to the desktop Azure Storage Explorer.

### Credential Management

- All credentials (tokens, SAS URLs, display names) are persisted in **browser localStorage**.
- **Export** credentials to a JSON file for backup or transfer.
- **Import** credentials from a previously exported JSON file.
- Credentials are stored with a user-defined display name for easy identification.

### File Browser

- **Sidebar tree view** — shows all connected storage accounts with expandable containers and virtual folder hierarchy.
- **Main panel** — lists blobs in the current container/folder with columns: Name, Size, Last Modified, Content Type.
- **Breadcrumb navigation** — click any segment to navigate up.
- **Sorting** — click column headers to sort.
- **Search/filter** — filter current listing by name.
- **Selection** — checkbox multi-select for bulk operations.

### Download

- **Single file download** — uses the File System Access API `showSaveFilePicker()` to let users choose the save location and filename.
- **Folder download** — uses `showDirectoryPicker()` to let users select a target directory; the app recursively downloads all blobs in the selected virtual folder, recreating the folder structure on disk. Users can create new folders during the picker flow.
- **Fallback** — if the browser does not support the File System Access API, falls back to classic `<a download>` for single files and a zip download for folders.

### Block List Management

- **View block list** — select a single blob and click "Block List" to see all committed and uncommitted blocks in a modal. Blocks are displayed sorted by name with their size and status (committed/uncommitted badge).
- **Commit block list** — from the block list modal, click "Commit Block List (sorted)" to commit all blocks (committed + uncommitted) sorted by block name. Uses the Put Block List REST API with `<Latest>` entries.
- **Folder-level commit** — click "Commit Folder" to commit blocks for all **zero-length blobs** in the current folder (or container root). The operation:
  1. Lists all blobs under the current prefix recursively.
  2. Filters to only blobs with `Content-Length: 0`.
  3. For each zero-length blob, fetches its block list and commits all blocks sorted by name.
  4. Skips blobs that have no blocks at all.
  5. Shows a progress modal with live count of committed/skipped.

### Upload (future)

- Placeholder for drag-and-drop and file picker upload support.

## Tech Stack

- **No build step** — pure HTML + CSS + ES modules loaded via CDN.
- **MSAL.js 2.x** — `@azure/msal-browser` via CDN for Azure AD auth.
- **Azure Storage Blob SDK** — `@azure/storage-blob` via CDN for blob operations.
- **File System Access API** — native browser API for save-to-disk.

## Project Structure

```
docs/azexplorer/
├── spec.md            # This file
├── index.html         # Entry point
├── css/
│   └── styles.css     # All styles
└── js/
    ├── app.js         # Main application bootstrap
    ├── auth.js        # MSAL + SAS authentication
    ├── credentials.js # localStorage CRUD, import/export
    ├── storage.js     # Azure Blob SDK wrapper, SAS URL parsing
    ├── ui.js          # DOM manipulation, tree view, file list, modals
    └── download.js    # File System Access API download logic
```

## Data Model

### Credential (localStorage)

```json
{
  "id": "uuid",
  "displayName": "My Storage Account",
  "type": "sas | azure-ad",
  "sasUrl": "https://proxy.example.com/container?sv=...",
  "accountName": "mystorageaccount",
  "tenantId": "...",
  "clientId": "...",
  "addedAt": "ISO-8601"
}
```

### Parsed SAS Connection

```json
{
  "baseUrl": "https://proxy.example.com",
  "accountName": "derived-or-user-provided",
  "containerName": "optional",
  "folderPrefix": "optional/path/",
  "sasToken": "sv=2022-11-02&ss=b&..."
}
```
