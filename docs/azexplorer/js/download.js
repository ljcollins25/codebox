/**
 * download.js — File download helpers using the File System Access API.
 *
 * - Single file: showSaveFilePicker() → stream blob to chosen location.
 * - Folder:      showDirectoryPicker() → recreate folder tree, stream each blob.
 * - Fallback:    classic <a download> for single files; no folder fallback without FS API.
 */

import { fetchBlob, listAllBlobs } from './storage.js';

/** Feature detection. */
export const hasFileSystemAccess = typeof window.showSaveFilePicker === 'function';
export const hasDirectoryPicker = typeof window.showDirectoryPicker === 'function';

// ─── Single file download ──────────────────────────────────────

/**
 * Download a single blob to a user-chosen location.
 * @param {import('./storage.js').StorageConnection} conn
 * @param {string} container
 * @param {string} blobName  — full blob name (may include virtual path)
 * @param {Function} [onProgress] — optional callback(downloaded, total)
 */
export async function downloadFile(conn, container, blobName, onProgress) {
    const fileName = blobName.split('/').pop() || blobName;

    if (hasFileSystemAccess) {
        try {
            const handle = await window.showSaveFilePicker({
                suggestedName: fileName,
            });
            const resp = await fetchBlob(conn, container, blobName);
            const total = parseInt(resp.headers.get('content-length') || '0', 10);
            const writable = await handle.createWritable();

            if (onProgress && resp.body) {
                const reader = resp.body.getReader();
                let downloaded = 0;
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    await writable.write(value);
                    downloaded += value.byteLength;
                    onProgress(downloaded, total);
                }
            } else {
                const blob = await resp.blob();
                await writable.write(blob);
            }

            await writable.close();
            return true;
        } catch (e) {
            if (e.name === 'AbortError') return false;
            throw e;
        }
    }

    // Fallback: classic <a download>
    const resp = await fetchBlob(conn, container, blobName);
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
    return true;
}

// ─── Folder download ───────────────────────────────────────────

/**
 * Get or create a sub-directory handle by path segments.
 * @param {FileSystemDirectoryHandle} root
 * @param {string[]} pathSegments
 * @returns {Promise<FileSystemDirectoryHandle>}
 */
async function getOrCreateDir(root, pathSegments) {
    let current = root;
    for (const segment of pathSegments) {
        if (!segment) continue;
        current = await current.getDirectoryHandle(segment, { create: true });
    }
    return current;
}

/**
 * Download an entire virtual folder to a user-chosen directory.
 *
 * @param {import('./storage.js').StorageConnection} conn
 * @param {string} container
 * @param {string} folderPrefix — e.g. "path/to/folder/"
 * @param {Function} [onProgress] — callback({ current, total, fileName })
 */
export async function downloadFolder(conn, container, folderPrefix, onProgress) {
    if (!hasDirectoryPicker) {
        throw new Error('Your browser does not support the File System Access API directory picker. Try Chrome or Edge.');
    }

    // Let user pick (and optionally create) a target directory.
    const rootHandle = await window.showDirectoryPicker({ mode: 'readwrite' });

    // List all blobs under the prefix.
    const allBlobs = await listAllBlobs(conn, container, folderPrefix);
    const total = allBlobs.length;

    for (let i = 0; i < allBlobs.length; i++) {
        const blob = allBlobs[i];
        const relativePath = blob.name.startsWith(folderPrefix)
            ? blob.name.slice(folderPrefix.length)
            : blob.name;

        const segments = relativePath.split('/');
        const fileName = segments.pop();
        if (!fileName) continue; // skip virtual directory markers

        if (onProgress) {
            onProgress({ current: i + 1, total, fileName });
        }

        // Create intermediate directories.
        const dirHandle = await getOrCreateDir(rootHandle, segments);

        // Create file and stream content.
        const fileHandle = await dirHandle.getFileHandle(fileName, { create: true });
        const writable = await fileHandle.createWritable();

        const resp = await fetchBlob(conn, container, blob.name);

        if (resp.body) {
            const reader = resp.body.getReader();
            while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                await writable.write(value);
            }
        } else {
            const data = await resp.blob();
            await writable.write(data);
        }

        await writable.close();
    }

    return { downloaded: total };
}
