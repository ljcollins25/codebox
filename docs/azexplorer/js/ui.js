/**
 * ui.js — DOM helpers: tree view, file listing, modals, breadcrumb, toasts.
 */

import { parseSasUrl, loginPopup } from './auth.js';
import { addCredential, removeCredential } from './credentials.js';

// ─── Icons (inline SVG strings) ────────────────────────────────

const ICONS = {
    account: `<svg class="node-icon" viewBox="0 0 24 24"><rect x="2" y="6" width="20" height="14" rx="2" stroke="#0078d4" stroke-width="1.5" fill="none"/><line x1="2" y1="10" x2="22" y2="10" stroke="#0078d4" stroke-width="1.5"/><circle cx="5" cy="8" r="0.8" fill="#0078d4"/></svg>`,
    container: `<svg class="node-icon" viewBox="0 0 24 24"><path d="M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z" stroke="#e6a817" stroke-width="1.5" fill="#fff3cd"/></svg>`,
    folder: `<svg class="node-icon" viewBox="0 0 24 24"><path d="M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z" stroke="#e6a817" stroke-width="1.5" fill="#fff3cd"/></svg>`,
    file: `<svg class="node-icon" viewBox="0 0 24 24"><path d="M6 2h8l6 6v12a2 2 0 01-2 2H6a2 2 0 01-2-2V4a2 2 0 012-2z" stroke="#666" stroke-width="1.5" fill="none"/><path d="M14 2v6h6" stroke="#666" stroke-width="1.5" fill="none"/></svg>`,
    chevron: `<svg class="toggle-icon" viewBox="0 0 16 16" width="12" height="12"><path d="M6 3l5 5-5 5" stroke="currentColor" stroke-width="1.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
    remove: `<svg viewBox="0 0 16 16" width="14" height="14"><path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>`,
};

// ─── Toast ─────────────────────────────────────────────────────

export function showToast(message, type = 'info', duration = 3500) {
    const container = document.getElementById('toast-container');
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.textContent = message;
    container.appendChild(toast);
    setTimeout(() => {
        toast.style.opacity = '0';
        toast.style.transition = 'opacity 0.3s';
        setTimeout(() => toast.remove(), 300);
    }, duration);
}

// ─── Modal ─────────────────────────────────────────────────────

export function openModal(title, bodyHtml) {
    document.getElementById('modal-title').textContent = title;
    document.getElementById('modal-body').innerHTML = bodyHtml;
    document.getElementById('modal-overlay').style.display = 'flex';
}

export function closeModal() {
    document.getElementById('modal-overlay').style.display = 'none';
}

// ─── Add Account Modal ────────────────────────────────────────

export function showAddAccountModal(onAdded) {
    const html = `
        <div class="form-tabs">
            <button class="form-tab active" data-tab="tab-sas">SAS URL</button>
            <button class="form-tab" data-tab="tab-aad">Azure AD</button>
        </div>
        <div id="tab-sas" class="tab-content active">
            <div class="form-group">
                <label for="sas-display-name">Display Name</label>
                <input type="text" id="sas-display-name" placeholder="e.g. My Storage Account">
            </div>
            <div class="form-group">
                <label for="sas-url">SAS URL</label>
                <textarea id="sas-url" rows="3" placeholder="https://host.com/container?sv=2022-11-02&ss=b&srt=sco&sp=rl&se=..."></textarea>
                <div class="form-hint">Supports account, container, or folder-level SAS URLs. Non-standard hostnames (proxy sites) are allowed.</div>
            </div>
            <div class="form-group">
                <label for="sas-account-name">Account Name (optional, auto-detected for standard URLs)</label>
                <input type="text" id="sas-account-name" placeholder="mystorageaccount">
            </div>
            <div class="modal-actions">
                <button class="btn-secondary" id="btn-cancel-add">Cancel</button>
                <button class="btn-primary" id="btn-add-sas">Add SAS Connection</button>
            </div>
        </div>
        <div id="tab-aad" class="tab-content">
            <div class="form-group">
                <label for="aad-display-name">Display Name</label>
                <input type="text" id="aad-display-name" placeholder="e.g. Work Account">
            </div>
            <div class="form-group">
                <label for="aad-account-name">Storage Account Name</label>
                <input type="text" id="aad-account-name" placeholder="mystorageaccount">
                <div class="form-hint">The name of the Azure Storage account to connect to.</div>
            </div>
            <div class="form-group">
                <label for="aad-client-id">Azure AD App Client ID</label>
                <input type="text" id="aad-client-id" placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx">
                <div class="form-hint">Register an app in Azure AD and add Storage permissions.</div>
            </div>
            <div class="form-group">
                <label for="aad-tenant-id">Tenant ID (optional, defaults to common)</label>
                <input type="text" id="aad-tenant-id" placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx">
            </div>
            <div class="modal-actions">
                <button class="btn-secondary" id="btn-cancel-add-aad">Cancel</button>
                <button class="btn-primary" id="btn-add-aad">Sign In &amp; Add</button>
            </div>
        </div>
    `;

    openModal('Add Account', html);

    // Tab switching
    document.querySelectorAll('.form-tab').forEach(tab => {
        tab.addEventListener('click', () => {
            document.querySelectorAll('.form-tab').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.tab-content').forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            document.getElementById(tab.dataset.tab).classList.add('active');
        });
    });

    // Cancel
    document.getElementById('btn-cancel-add')?.addEventListener('click', closeModal);
    document.getElementById('btn-cancel-add-aad')?.addEventListener('click', closeModal);

    // Add SAS
    document.getElementById('btn-add-sas').addEventListener('click', () => {
        const rawUrl = document.getElementById('sas-url').value.trim();
        const displayName = document.getElementById('sas-display-name').value.trim();
        const manualAccountName = document.getElementById('sas-account-name').value.trim();

        if (!rawUrl) {
            showToast('Please enter a SAS URL', 'error');
            return;
        }

        try {
            const parsed = parseSasUrl(rawUrl);
            const cred = addCredential({
                type: 'sas',
                displayName: displayName || parsed.accountName || parsed.host,
                sasUrl: rawUrl,
                baseUrl: parsed.baseUrl,
                sasToken: parsed.sasToken,
                accountName: manualAccountName || parsed.accountName,
                containerName: parsed.containerName,
                folderPrefix: parsed.folderPrefix,
                host: parsed.host,
            });
            closeModal();
            showToast(`Added "${cred.displayName}"`, 'success');
            onAdded(cred);
        } catch (e) {
            showToast(`Invalid URL: ${e.message}`, 'error');
        }
    });

    // Add Azure AD
    document.getElementById('btn-add-aad').addEventListener('click', async () => {
        const displayName = document.getElementById('aad-display-name').value.trim();
        const accountName = document.getElementById('aad-account-name').value.trim();
        const clientId = document.getElementById('aad-client-id').value.trim();
        const tenantId = document.getElementById('aad-tenant-id').value.trim();

        if (!accountName || !clientId) {
            showToast('Storage account name and Client ID are required', 'error');
            return;
        }

        try {
            const authResult = await loginPopup(clientId, tenantId);
            const cred = addCredential({
                type: 'azure-ad',
                displayName: displayName || accountName,
                accountName,
                clientId,
                tenantId,
                accountHomeId: authResult.account.homeAccountId,
                accessToken: authResult.accessToken,
                expiresOn: authResult.expiresOn?.toISOString(),
            });
            closeModal();
            showToast(`Signed in as ${authResult.account.username}`, 'success');
            onAdded(cred);
        } catch (e) {
            showToast(`Auth failed: ${e.message}`, 'error');
        }
    });
}

// ─── Account Tree ──────────────────────────────────────────────

/**
 * Render the sidebar account tree.
 * @param {Array} credentials
 * @param {Object} callbacks — { onSelectAccount, onSelectContainer, onSelectFolder, onRemoveAccount }
 */
export function renderAccountTree(credentials, callbacks) {
    const tree = document.getElementById('account-tree');
    tree.innerHTML = '';

    if (credentials.length === 0) {
        tree.innerHTML = `
            <div style="padding: 20px 12px; color: var(--color-text-secondary); font-size: 12px; text-align: center;">
                No accounts added yet.<br>Click <b>Add Account</b> to get started.
            </div>`;
        return;
    }

    for (const cred of credentials) {
        const accountNode = createTreeNode({
            label: cred.displayName || cred.accountName || cred.host || 'Storage Account',
            icon: ICONS.account,
            depth: 0,
            expandable: true,
            onToggle: (node, expanded) => {
                if (expanded) {
                    callbacks.onExpandAccount(cred, node);
                } else {
                    // Collapse: remove children
                    collapseChildren(node);
                }
            },
            onClick: () => callbacks.onSelectAccount(cred),
            actions: [
                { icon: ICONS.remove, title: 'Remove', onClick: () => callbacks.onRemoveAccount(cred) },
            ],
        });
        accountNode.dataset.credId = cred.id;
        tree.appendChild(accountNode);
    }
}

/**
 * Add container nodes under an account node.
 */
export function addContainerNodes(parentNode, containers, cred, callbacks) {
    collapseChildren(parentNode);
    for (const container of containers) {
        const node = createTreeNode({
            label: container.name,
            icon: ICONS.container,
            depth: 1,
            expandable: true,
            onToggle: (node, expanded) => {
                if (expanded) {
                    callbacks.onExpandContainer(cred, container.name, '', node);
                } else {
                    collapseChildren(node);
                }
            },
            onClick: () => callbacks.onSelectContainer(cred, container.name, ''),
        });
        node.dataset.containerName = container.name;
        parentNode.after(node);
        // Insert after previous sibling so order is preserved
        parentNode = node;
    }
}

/**
 * Add folder/blob nodes under a container or folder node.
 */
export function addFolderNodes(parentNode, prefixes, cred, container, callbacks) {
    for (let i = prefixes.length - 1; i >= 0; i--) {
        const prefix = prefixes[i];
        const depth = parseInt(parentNode.style.getPropertyValue('--depth') || '1') + 1;
        const node = createTreeNode({
            label: prefix.name,
            icon: ICONS.folder,
            depth,
            expandable: true,
            onToggle: (node, expanded) => {
                if (expanded) {
                    callbacks.onExpandContainer(cred, container, prefix.fullName, node);
                } else {
                    collapseChildren(node);
                }
            },
            onClick: () => callbacks.onSelectContainer(cred, container, prefix.fullName),
        });
        node.dataset.prefix = prefix.fullName;
        insertAfter(parentNode, node);
    }
}

function insertAfter(referenceNode, newNode) {
    referenceNode.parentNode.insertBefore(newNode, referenceNode.nextSibling);
}

function collapseChildren(parentNode) {
    const parentDepth = parseInt(parentNode.style.getPropertyValue('--depth') || '0');
    let next = parentNode.nextElementSibling;
    while (next) {
        const nextDepth = parseInt(next.style.getPropertyValue('--depth') || '0');
        if (nextDepth <= parentDepth) break;
        const toRemove = next;
        next = next.nextElementSibling;
        toRemove.remove();
    }
}

function createTreeNode({ label, icon, depth, expandable, onToggle, onClick, actions }) {
    const div = document.createElement('div');
    div.className = 'tree-node';
    div.style.setProperty('--depth', depth);

    const toggle = document.createElement('span');
    toggle.className = expandable ? 'toggle' : 'toggle hidden';
    toggle.innerHTML = ICONS.chevron;
    div.appendChild(toggle);

    const iconSpan = document.createElement('span');
    iconSpan.innerHTML = icon;
    div.appendChild(iconSpan);

    const labelSpan = document.createElement('span');
    labelSpan.className = 'node-label';
    labelSpan.textContent = label;
    labelSpan.title = label;
    div.appendChild(labelSpan);

    // Actions
    if (actions && actions.length) {
        const actionsDiv = document.createElement('span');
        actionsDiv.className = 'node-actions';
        for (const action of actions) {
            const btn = document.createElement('button');
            btn.innerHTML = action.icon;
            btn.title = action.title;
            btn.addEventListener('click', e => {
                e.stopPropagation();
                action.onClick();
            });
            actionsDiv.appendChild(btn);
        }
        div.appendChild(actionsDiv);
    }

    let expanded = false;
    toggle.addEventListener('click', e => {
        e.stopPropagation();
        expanded = !expanded;
        toggle.classList.toggle('expanded', expanded);
        onToggle?.(div, expanded);
    });

    div.addEventListener('click', () => {
        if (onClick) onClick();
        // Visual selection
        document.querySelectorAll('.tree-node.selected').forEach(n => n.classList.remove('selected'));
        div.classList.add('selected');
    });

    // Double-click to expand/collapse
    div.addEventListener('dblclick', () => {
        if (expandable) {
            expanded = !expanded;
            toggle.classList.toggle('expanded', expanded);
            onToggle?.(div, expanded);
        }
    });

    return div;
}

// ─── Breadcrumb ────────────────────────────────────────────────

export function renderBreadcrumb(accountName, container, prefix, onNavigate) {
    const bc = document.getElementById('breadcrumb');
    bc.innerHTML = '';

    const segments = [];
    segments.push({ label: accountName, path: { container: null, prefix: '' } });
    if (container) {
        segments.push({ label: container, path: { container, prefix: '' } });
    }
    if (prefix) {
        const parts = prefix.replace(/\/$/, '').split('/');
        let running = '';
        for (const part of parts) {
            running += part + '/';
            segments.push({ label: part, path: { container, prefix: running } });
        }
    }

    segments.forEach((seg, i) => {
        if (i > 0) {
            const sep = document.createElement('span');
            sep.className = 'separator';
            sep.textContent = '›';
            bc.appendChild(sep);
        }
        const link = document.createElement('a');
        link.textContent = seg.label;
        link.addEventListener('click', () => onNavigate(seg.path));
        bc.appendChild(link);
    });
}

// ─── File List ─────────────────────────────────────────────────

/**
 * Render file/folder rows in the file list table.
 * @param {Array} items — mixed folders (isFolder=true) and blobs.
 * @param {Object} callbacks — { onNavigateFolder, onSelectItems }
 */
export function renderFileList(items, callbacks) {
    const tbody = document.getElementById('file-list-body');
    const empty = document.getElementById('empty-state');
    const loading = document.getElementById('loading-state');
    loading.style.display = 'none';

    tbody.innerHTML = '';

    if (items.length === 0) {
        empty.style.display = 'flex';
        return;
    }
    empty.style.display = 'none';

    for (const item of items) {
        const tr = document.createElement('tr');
        tr.dataset.fullName = item.fullName || item.name;
        tr.dataset.isFolder = item.isFolder;

        // Checkbox
        const tdCheck = document.createElement('td');
        tdCheck.className = 'col-check';
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.addEventListener('change', () => {
            tr.classList.toggle('selected', cb.checked);
            callbacks.onSelectItems?.();
        });
        tdCheck.appendChild(cb);
        tr.appendChild(tdCheck);

        // Name
        const tdName = document.createElement('td');
        const nameDiv = document.createElement('div');
        nameDiv.className = 'name-cell';
        const iconSpan = document.createElement('span');
        iconSpan.innerHTML = item.isFolder ? ICONS.folder : ICONS.file;
        nameDiv.appendChild(iconSpan);
        const nameText = document.createElement('span');
        nameText.textContent = item.name;
        nameText.title = item.name;
        nameDiv.appendChild(nameText);
        tdName.appendChild(nameDiv);
        tr.appendChild(tdName);

        // Size
        const tdSize = document.createElement('td');
        tdSize.textContent = item.isFolder ? '—' : formatSize(item.size);
        tr.appendChild(tdSize);

        // Last modified
        const tdMod = document.createElement('td');
        tdMod.textContent = item.lastModified ? new Date(item.lastModified).toLocaleString() : '—';
        tr.appendChild(tdMod);

        // Content type
        const tdType = document.createElement('td');
        tdType.textContent = item.isFolder ? 'Folder' : (item.contentType || '—');
        tr.appendChild(tdType);

        // Double-click to navigate into folders
        tr.addEventListener('dblclick', () => {
            if (item.isFolder) {
                callbacks.onNavigateFolder(item);
            }
        });

        tbody.appendChild(tr);
    }

    updateStatusCount(items);
}

export function showLoading() {
    document.getElementById('file-list-body').innerHTML = '';
    document.getElementById('empty-state').style.display = 'none';
    document.getElementById('loading-state').style.display = 'flex';
}

export function hideLoading() {
    document.getElementById('loading-state').style.display = 'none';
}

export function setStatus(text) {
    document.getElementById('status-text').textContent = text;
}

function updateStatusCount(items) {
    const folders = items.filter(i => i.isFolder).length;
    const files = items.length - folders;
    document.getElementById('status-count').textContent =
        `${files} file${files !== 1 ? 's' : ''}, ${folders} folder${folders !== 1 ? 's' : ''}`;
}

export function getSelectedItems() {
    const rows = document.querySelectorAll('#file-list-body tr');
    const selected = [];
    rows.forEach(tr => {
        const cb = tr.querySelector('input[type="checkbox"]');
        if (cb?.checked) {
            selected.push({
                fullName: tr.dataset.fullName,
                isFolder: tr.dataset.isFolder === 'true',
            });
        }
    });
    return selected;
}

// ─── Progress overlay ──────────────────────────────────────────

let progressEl = null;

export function showProgress(title) {
    if (progressEl) progressEl.remove();
    progressEl = document.createElement('div');
    progressEl.className = 'progress-overlay';
    progressEl.innerHTML = `
        <div class="progress-card">
            <div class="progress-title">${title}</div>
            <div class="progress-bar-track"><div class="progress-bar-fill" style="width:0%"></div></div>
            <div class="progress-detail">Starting...</div>
        </div>`;
    document.body.appendChild(progressEl);
}

export function updateProgress(percent, detail) {
    if (!progressEl) return;
    const fill = progressEl.querySelector('.progress-bar-fill');
    const det = progressEl.querySelector('.progress-detail');
    if (fill) fill.style.width = `${Math.min(100, percent)}%`;
    if (det) det.textContent = detail;
}

export function hideProgress() {
    if (progressEl) {
        progressEl.remove();
        progressEl = null;
    }
}

// ─── Sorting ───────────────────────────────────────────────────

export function setupSorting(onSort) {
    document.querySelectorAll('#file-list th.sortable').forEach(th => {
        th.addEventListener('click', () => {
            const field = th.dataset.sort;
            const wasAsc = th.classList.contains('sort-asc');
            // Reset all
            document.querySelectorAll('#file-list th.sortable').forEach(h => {
                h.classList.remove('sort-asc', 'sort-desc');
            });
            const dir = wasAsc ? 'desc' : 'asc';
            th.classList.add(`sort-${dir}`);
            onSort(field, dir);
        });
    });
}

// ─── Filtering ─────────────────────────────────────────────────

export function setupFilter(onFilter) {
    const input = document.getElementById('filter-input');
    let timer;
    input.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(() => onFilter(input.value.trim().toLowerCase()), 150);
    });
}

// ─── Sidebar resize ────────────────────────────────────────────

export function setupSidebarResize() {
    const handle = document.getElementById('sidebar-resize-handle');
    const sidebar = document.getElementById('sidebar');
    let startX, startWidth;

    handle.addEventListener('mousedown', e => {
        startX = e.clientX;
        startWidth = sidebar.offsetWidth;
        handle.classList.add('active');
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
        e.preventDefault();
    });

    function onMouseMove(e) {
        const newWidth = startWidth + (e.clientX - startX);
        sidebar.style.width = `${Math.max(180, Math.min(500, newWidth))}px`;
    }

    function onMouseUp() {
        handle.classList.remove('active');
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
    }
}

// ─── Block List Modal ──────────────────────────────────────────

/**
 * Show a modal displaying block list for a blob, with a Commit button.
 * @param {Object} blockListData — { committedBlocks, uncommittedBlocks }
 * @param {string} blobName
 * @param {Function} onCommit — called with combined blocks array when user clicks Commit
 */
export function showBlockListModal(blockListData, blobName, onCommit) {
    const { committedBlocks, uncommittedBlocks } = blockListData;
    const allBlocks = [
        ...committedBlocks.map(b => ({ ...b, status: 'committed' })),
        ...uncommittedBlocks.map(b => ({ ...b, status: 'uncommitted' })),
    ];
    const sorted = [...allBlocks].sort((a, b) => a.name.localeCompare(b.name));

    const fileName = blobName.split('/').pop() || blobName;
    const totalSize = allBlocks.reduce((s, b) => s + b.size, 0);

    let tableRows = '';
    if (sorted.length === 0) {
        tableRows = '<tr><td colspan="4" style="text-align:center;color:var(--color-text-secondary);padding:20px;">No blocks found</td></tr>';
    } else {
        sorted.forEach((block, idx) => {
            const badge = block.status === 'committed'
                ? '<span class="badge badge-committed">Committed</span>'
                : '<span class="badge badge-uncommitted">Uncommitted</span>';
            tableRows += `<tr>
                <td style="text-align:center;">${idx + 1}</td>
                <td class="block-name-cell" title="${escapeHtmlAttr(block.name)}">${escapeHtmlContent(block.name)}</td>
                <td>${formatSize(block.size)}</td>
                <td>${badge}</td>
            </tr>`;
        });
    }

    const html = `
        <div class="blocklist-summary">
            <span><strong>Blob:</strong> ${escapeHtmlContent(fileName)}</span>
            <span><strong>Committed:</strong> ${committedBlocks.length}</span>
            <span><strong>Uncommitted:</strong> ${uncommittedBlocks.length}</span>
            <span><strong>Total size:</strong> ${formatSize(totalSize)}</span>
        </div>
        <div class="blocklist-table-wrap">
            <table class="blocklist-table">
                <thead>
                    <tr>
                        <th style="width:50px">#</th>
                        <th>Block Name</th>
                        <th style="width:100px">Size</th>
                        <th style="width:110px">Status</th>
                    </tr>
                </thead>
                <tbody>${tableRows}</tbody>
            </table>
        </div>
        <div class="modal-actions">
            <button class="btn-secondary" id="btn-blocklist-close">Close</button>
            ${allBlocks.length > 0 ? '<button class="btn-primary" id="btn-blocklist-commit">Commit Block List (sorted)</button>' : ''}
        </div>
    `;

    openModal(`Block List — ${fileName}`, html);

    document.getElementById('btn-blocklist-close')?.addEventListener('click', closeModal);
    document.getElementById('btn-blocklist-commit')?.addEventListener('click', () => {
        onCommit(allBlocks);
    });
}

/**
 * Show progress modal for folder-level commit operation.
 * @param {number} totalBlobs
 * @param {Function} onCancel
 * @returns {{ update(current, blobName, skipped), finish(committed, skipped) }}
 */
export function showFolderCommitProgress(totalBlobs, onCancel) {
    let cancelled = false;
    const html = `
        <div class="folder-commit-progress">
            <div class="progress-bar-track"><div class="progress-bar-fill" id="fc-progress-bar" style="width:0%"></div></div>
            <div id="fc-status" class="fc-status">Scanning blobs...</div>
            <div id="fc-detail" class="fc-detail"></div>
        </div>
        <div class="modal-actions">
            <button class="btn-secondary" id="btn-fc-cancel">Cancel</button>
        </div>
    `;

    openModal(`Commit Blocks — Folder (${totalBlobs} blobs)`, html);

    document.getElementById('btn-fc-cancel')?.addEventListener('click', () => {
        cancelled = true;
        onCancel();
        closeModal();
    });

    return {
        isCancelled: () => cancelled,
        update(current, blobName, skipped) {
            const pct = totalBlobs > 0 ? (current / totalBlobs) * 100 : 0;
            const bar = document.getElementById('fc-progress-bar');
            const status = document.getElementById('fc-status');
            const detail = document.getElementById('fc-detail');
            if (bar) bar.style.width = `${pct}%`;
            if (status) status.textContent = `Processing ${current}/${totalBlobs}...`;
            if (detail) detail.textContent = `${blobName} (${skipped} skipped)`;
        },
        finish(committed, skipped) {
            const status = document.getElementById('fc-status');
            const detail = document.getElementById('fc-detail');
            const bar = document.getElementById('fc-progress-bar');
            if (bar) bar.style.width = '100%';
            if (status) status.textContent = 'Done!';
            if (detail) detail.textContent = `Committed: ${committed}, Skipped (non-zero or no blocks): ${skipped}`;
            const cancelBtn = document.getElementById('btn-fc-cancel');
            if (cancelBtn) { cancelBtn.textContent = 'Close'; cancelBtn.onclick = closeModal; }
        },
    };
}

function escapeHtmlContent(str) {
    const d = document.createElement('div');
    d.textContent = str;
    return d.innerHTML;
}

function escapeHtmlAttr(str) {
    return str.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// ─── Helpers ───────────────────────────────────────────────────

function formatSize(bytes) {
    if (bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return (bytes / Math.pow(1024, i)).toFixed(i > 0 ? 1 : 0) + ' ' + units[i];
}
