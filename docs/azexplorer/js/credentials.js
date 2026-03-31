/**
 * credentials.js — localStorage CRUD + import/export for storage credentials.
 */

const STORAGE_KEY = 'azexplorer_credentials';

/** Generate a simple UUID v4. */
function uuid() {
    return crypto.randomUUID?.() ?? 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
        const r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}

/** Load all credentials from localStorage. @returns {Array} */
export function loadCredentials() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        return raw ? JSON.parse(raw) : [];
    } catch {
        return [];
    }
}

/** Save all credentials to localStorage. */
export function saveCredentials(credentials) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(credentials));
}

/** Add a new credential and persist. @returns the credential with id. */
export function addCredential(cred) {
    const credentials = loadCredentials();
    const entry = {
        id: uuid(),
        addedAt: new Date().toISOString(),
        ...cred,
    };
    credentials.push(entry);
    saveCredentials(credentials);
    return entry;
}

/** Remove a credential by id. */
export function removeCredential(id) {
    const credentials = loadCredentials().filter(c => c.id !== id);
    saveCredentials(credentials);
}

/** Update a credential by id. */
export function updateCredential(id, updates) {
    const credentials = loadCredentials();
    const idx = credentials.findIndex(c => c.id === id);
    if (idx !== -1) {
        credentials[idx] = { ...credentials[idx], ...updates };
        saveCredentials(credentials);
    }
}

/**
 * Export credentials to a JSON file.
 * Uses File System Access API if available, else falls back to <a> download.
 */
export async function exportCredentials() {
    const credentials = loadCredentials();
    const json = JSON.stringify(credentials, null, 2);
    const blob = new Blob([json], { type: 'application/json' });

    if (window.showSaveFilePicker) {
        try {
            const handle = await window.showSaveFilePicker({
                suggestedName: 'azexplorer-credentials.json',
                types: [{ description: 'JSON Files', accept: { 'application/json': ['.json'] } }],
            });
            const writable = await handle.createWritable();
            await writable.write(blob);
            await writable.close();
            return true;
        } catch (e) {
            if (e.name === 'AbortError') return false;
            throw e;
        }
    }

    // Fallback
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'azexplorer-credentials.json';
    a.click();
    URL.revokeObjectURL(url);
    return true;
}

/**
 * Import credentials from a JSON file. Merges with existing (skips duplicate ids).
 * @param {File} file
 * @returns {number} count of imported credentials
 */
export async function importCredentials(file) {
    const text = await file.text();
    const incoming = JSON.parse(text);
    if (!Array.isArray(incoming)) throw new Error('Invalid credentials file');

    const existing = loadCredentials();
    const existingIds = new Set(existing.map(c => c.id));
    let count = 0;
    for (const cred of incoming) {
        if (!cred.id || existingIds.has(cred.id)) {
            // Assign a new id if missing or duplicate
            cred.id = uuid();
        }
        existing.push(cred);
        count++;
    }
    saveCredentials(existing);
    return count;
}
