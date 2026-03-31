/**
 * app.js — Main application entry point.
 * Wires together auth, credentials, storage, UI, and download modules.
 */

import { loadCredentials, removeCredential, exportCredentials, importCredentials } from './credentials.js';
import { connectionFromCredential, listContainers, listBlobs } from './storage.js';
import { downloadFile, downloadFolder, hasFileSystemAccess, hasDirectoryPicker } from './download.js';
import { acquireTokenSilent } from './auth.js';
import {
    showToast, showAddAccountModal, renderAccountTree,
    addContainerNodes, addFolderNodes, renderBreadcrumb,
    renderFileList, showLoading, hideLoading, setStatus,
    getSelectedItems, showProgress, updateProgress, hideProgress,
    setupSorting, setupFilter, setupSidebarResize, closeModal,
} from './ui.js';

// ─── Application state ────────────────────────────────────────

let currentCred = null;        // currently selected credential record
let currentConn = null;        // StorageConnection for the current credential
let currentContainer = '';     // current container name
let currentPrefix = '';        // current virtual folder prefix
let currentItems = [];         // items in the current view (folders + blobs)
let allRawItems = [];          // un-filtered items

// ─── Initialisation ────────────────────────────────────────────

document.addEventListener('DOMContentLoaded', () => {
    setupSidebarResize();
    setupToolbarButtons();
    setupSorting(handleSort);
    setupFilter(handleFilter);
    setupSelectAll();
    refreshTree();
});

// ─── Toolbar wiring ────────────────────────────────────────────

function setupToolbarButtons() {
    document.getElementById('btn-add-account').addEventListener('click', () => {
        showAddAccountModal(cred => {
            refreshTree();
            // If SAS URL has a container, auto-navigate
            if (cred.type === 'sas' && cred.containerName) {
                navigateTo(cred, cred.containerName, cred.folderPrefix || '');
            }
        });
    });

    document.getElementById('btn-export-creds').addEventListener('click', async () => {
        try {
            const ok = await exportCredentials();
            if (ok) showToast('Credentials exported', 'success');
        } catch (e) {
            showToast(`Export failed: ${e.message}`, 'error');
        }
    });

    document.getElementById('btn-import-creds').addEventListener('click', () => {
        document.getElementById('import-file-input').click();
    });

    document.getElementById('import-file-input').addEventListener('change', async (e) => {
        const file = e.target.files[0];
        if (!file) return;
        try {
            const count = await importCredentials(file);
            showToast(`Imported ${count} credential(s)`, 'success');
            refreshTree();
        } catch (err) {
            showToast(`Import failed: ${err.message}`, 'error');
        }
        e.target.value = '';
    });

    document.getElementById('modal-close').addEventListener('click', closeModal);
    document.getElementById('modal-overlay').addEventListener('click', e => {
        if (e.target === e.currentTarget) closeModal();
    });

    // Download buttons
    document.getElementById('btn-download').addEventListener('click', handleDownloadSelected);
    document.getElementById('btn-download-folder').addEventListener('click', handleDownloadFolder);
    document.getElementById('btn-refresh').addEventListener('click', () => {
        if (currentCred && currentContainer) {
            navigateTo(currentCred, currentContainer, currentPrefix);
        }
    });
}

// ─── Tree refresh ──────────────────────────────────────────────

function refreshTree() {
    const credentials = loadCredentials();
    renderAccountTree(credentials, {
        onExpandAccount: handleExpandAccount,
        onSelectAccount: handleSelectAccount,
        onSelectContainer: handleSelectContainer,
        onRemoveAccount: handleRemoveAccount,
    });
}

// ─── Tree callbacks ────────────────────────────────────────────

async function handleExpandAccount(cred, node) {
    try {
        const conn = await getConnection(cred);

        // If this SAS is scoped to a specific container, show just that.
        if (cred.type === 'sas' && cred.containerName) {
            addContainerNodes(node, [{ name: cred.containerName }], cred, {
                onExpandContainer: handleExpandContainer,
                onSelectContainer: (cr, container, prefix) => navigateTo(cr, container, prefix),
            });
            return;
        }

        setStatus('Loading containers...');
        const containers = await listContainers(conn);
        addContainerNodes(node, containers, cred, {
            onExpandContainer: handleExpandContainer,
            onSelectContainer: (cr, container, prefix) => navigateTo(cr, container, prefix),
        });
        setStatus('Ready');
    } catch (e) {
        showToast(`Failed to list containers: ${e.message}`, 'error');
        setStatus('Error');
    }
}

async function handleExpandContainer(cred, container, prefix, node) {
    try {
        const conn = await getConnection(cred);
        setStatus('Loading...');
        const result = await listBlobs(conn, container, prefix);
        addFolderNodes(node, result.prefixes, cred, container, {
            onExpandContainer: handleExpandContainer,
            onSelectContainer: (cr, cont, pfx) => navigateTo(cr, cont, pfx),
        });
        setStatus('Ready');
    } catch (e) {
        showToast(`Failed to expand: ${e.message}`, 'error');
        setStatus('Error');
    }
}

function handleSelectAccount(cred) {
    currentCred = cred;
    currentConn = null;
    currentContainer = '';
    currentPrefix = '';
    renderBreadcrumb(cred.displayName || cred.accountName || cred.host, null, '', handleBreadcrumbNavigate);
    renderFileList([], { onNavigateFolder: () => {}, onSelectItems: updateActionButtons });
    updateActionButtons();
}

function handleSelectContainer(cred, container, prefix) {
    navigateTo(cred, container, prefix);
}

function handleRemoveAccount(cred) {
    if (!confirm(`Remove "${cred.displayName}"?`)) return;
    removeCredential(cred.id);
    showToast(`Removed "${cred.displayName}"`, 'info');
    refreshTree();
    // Clear view if we were looking at this account
    if (currentCred?.id === cred.id) {
        currentCred = null;
        currentConn = null;
        currentItems = [];
        document.getElementById('breadcrumb').innerHTML =
            '<span class="breadcrumb-placeholder">Select a storage account to begin</span>';
        renderFileList([], { onNavigateFolder: () => {}, onSelectItems: updateActionButtons });
        updateActionButtons();
    }
}

// ─── Navigation ────────────────────────────────────────────────

async function navigateTo(cred, container, prefix) {
    currentCred = cred;
    currentContainer = container;
    currentPrefix = prefix;

    try {
        const conn = await getConnection(cred);
        currentConn = conn;

        renderBreadcrumb(
            cred.displayName || cred.accountName || cred.host,
            container,
            prefix,
            handleBreadcrumbNavigate
        );

        showLoading();
        enableActionBar(true);

        const result = await listBlobs(conn, container, prefix);
        allRawItems = [...result.prefixes, ...result.blobs];
        currentItems = [...allRawItems];
        renderFileList(currentItems, {
            onNavigateFolder: item => navigateTo(cred, container, item.fullName),
            onSelectItems: updateActionButtons,
        });
        setStatus('Ready');
        updateActionButtons();
    } catch (e) {
        hideLoading();
        showToast(`Error: ${e.message}`, 'error');
        setStatus('Error');
    }
}

function handleBreadcrumbNavigate({ container, prefix }) {
    if (!currentCred) return;
    if (container === null) {
        // Account level — show nothing in file list
        handleSelectAccount(currentCred);
        return;
    }
    navigateTo(currentCred, container, prefix);
}

// ─── Connection helper ─────────────────────────────────────────

async function getConnection(cred) {
    const conn = connectionFromCredential(cred);

    // For Azure AD, refresh token if needed.
    if (conn.type === 'azure-ad') {
        try {
            const tokenResult = await acquireTokenSilent(cred.clientId, cred.tenantId, cred.accountHomeId);
            conn.accessToken = tokenResult.accessToken;
        } catch {
            // Token might still be valid from credential
        }
    }

    return conn;
}

// ─── Action buttons ────────────────────────────────────────────

function enableActionBar(enabled) {
    document.getElementById('btn-refresh').disabled = !enabled;
    document.getElementById('filter-input').disabled = !enabled;
}

function updateActionButtons() {
    const selected = getSelectedItems();
    const hasFiles = selected.some(s => !s.isFolder);
    const hasFolders = selected.some(s => s.isFolder);

    document.getElementById('btn-download').disabled = !hasFiles;
    document.getElementById('btn-download-folder').disabled = !(hasFolders || currentPrefix);
}

function setupSelectAll() {
    document.getElementById('select-all').addEventListener('change', e => {
        const checked = e.target.checked;
        document.querySelectorAll('#file-list-body input[type="checkbox"]').forEach(cb => {
            cb.checked = checked;
            cb.closest('tr').classList.toggle('selected', checked);
        });
        updateActionButtons();
    });
}

// ─── Download handlers ─────────────────────────────────────────

async function handleDownloadSelected() {
    const selected = getSelectedItems().filter(s => !s.isFolder);
    if (selected.length === 0) return;

    if (selected.length === 1) {
        try {
            showProgress('Downloading file...');
            await downloadFile(currentConn, currentContainer, selected[0].fullName, (downloaded, total) => {
                const pct = total > 0 ? (downloaded / total) * 100 : 50;
                updateProgress(pct, `${formatBytes(downloaded)} / ${formatBytes(total)}`);
            });
            hideProgress();
            showToast('Download complete', 'success');
        } catch (e) {
            hideProgress();
            if (e.name !== 'AbortError') showToast(`Download failed: ${e.message}`, 'error');
        }
        return;
    }

    // Multiple files: download one by one (or into a directory).
    if (hasDirectoryPicker) {
        try {
            const dirHandle = await window.showDirectoryPicker({ mode: 'readwrite' });
            showProgress(`Downloading ${selected.length} files...`);

            for (let i = 0; i < selected.length; i++) {
                const item = selected[i];
                const fileName = item.fullName.split('/').pop();
                updateProgress(((i + 1) / selected.length) * 100, `${i + 1}/${selected.length}: ${fileName}`);

                const fileHandle = await dirHandle.getFileHandle(fileName, { create: true });
                const writable = await fileHandle.createWritable();
                const resp = await (await import('./storage.js')).fetchBlob(currentConn, currentContainer, item.fullName);

                if (resp.body) {
                    const reader = resp.body.getReader();
                    while (true) {
                        const { done, value } = await reader.read();
                        if (done) break;
                        await writable.write(value);
                    }
                } else {
                    await writable.write(await resp.blob());
                }
                await writable.close();
            }

            hideProgress();
            showToast(`Downloaded ${selected.length} files`, 'success');
        } catch (e) {
            hideProgress();
            if (e.name !== 'AbortError') showToast(`Download failed: ${e.message}`, 'error');
        }
    } else {
        // Fallback: sequential classic downloads
        for (const item of selected) {
            try {
                await downloadFile(currentConn, currentContainer, item.fullName);
            } catch (e) {
                showToast(`Failed: ${item.fullName.split('/').pop()}`, 'error');
            }
        }
    }
}

async function handleDownloadFolder() {
    const selected = getSelectedItems().filter(s => s.isFolder);
    const prefix = selected.length > 0 ? selected[0].fullName : currentPrefix;

    if (!prefix) {
        showToast('No folder selected', 'warning');
        return;
    }

    if (!hasDirectoryPicker) {
        showToast('Your browser does not support folder downloads. Try Chrome or Edge.', 'warning');
        return;
    }

    try {
        showProgress('Downloading folder...');
        const result = await downloadFolder(currentConn, currentContainer, prefix, ({ current, total, fileName }) => {
            updateProgress((current / total) * 100, `${current}/${total}: ${fileName}`);
        });
        hideProgress();
        showToast(`Downloaded ${result.downloaded} files`, 'success');
    } catch (e) {
        hideProgress();
        if (e.name !== 'AbortError') showToast(`Folder download failed: ${e.message}`, 'error');
    }
}

// ─── Sorting / Filtering ──────────────────────────────────────

function handleSort(field, dir) {
    const sorted = [...currentItems].sort((a, b) => {
        // Folders always first
        if (a.isFolder !== b.isFolder) return a.isFolder ? -1 : 1;

        let va, vb;
        switch (field) {
            case 'name': va = a.name.toLowerCase(); vb = b.name.toLowerCase(); break;
            case 'size': va = a.size; vb = b.size; break;
            case 'modified': va = new Date(a.lastModified || 0).getTime(); vb = new Date(b.lastModified || 0).getTime(); break;
            case 'type': va = (a.contentType || '').toLowerCase(); vb = (b.contentType || '').toLowerCase(); break;
            default: return 0;
        }
        if (va < vb) return dir === 'asc' ? -1 : 1;
        if (va > vb) return dir === 'asc' ? 1 : -1;
        return 0;
    });

    currentItems = sorted;
    renderFileList(currentItems, {
        onNavigateFolder: item => navigateTo(currentCred, currentContainer, item.fullName),
        onSelectItems: updateActionButtons,
    });
}

function handleFilter(query) {
    if (!query) {
        currentItems = [...allRawItems];
    } else {
        currentItems = allRawItems.filter(item => item.name.toLowerCase().includes(query));
    }
    renderFileList(currentItems, {
        onNavigateFolder: item => navigateTo(currentCred, currentContainer, item.fullName),
        onSelectItems: updateActionButtons,
    });
}

// ─── Utils ─────────────────────────────────────────────────────

function formatBytes(bytes) {
    if (!bytes || bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return (bytes / Math.pow(1024, i)).toFixed(i > 0 ? 1 : 0) + ' ' + units[i];
}
