/**
 * storage.js — Azure Blob Storage operations.
 *
 * Works with both SAS URLs (including non-standard proxy hosts)
 * and Azure AD bearer tokens.
 *
 * Uses raw REST calls to the Azure Blob Storage API so we can support
 * arbitrary base URLs (proxy sites) without the SDK forcing *.blob.core.windows.net.
 */

/**
 * @typedef {Object} StorageConnection
 * @property {'sas'|'azure-ad'} type
 * @property {string} [sasToken]       — for SAS connections
 * @property {string} [baseUrl]        — e.g. https://proxysite.com or https://acct.blob.core.windows.net
 * @property {string} [containerName]  — scoped container (optional)
 * @property {string} [folderPrefix]   — scoped folder (optional)
 * @property {string} [accessToken]    — for Azure AD connections
 * @property {string} [accountName]    — for Azure AD connections
 */

const API_VERSION = '2023-11-03';

/**
 * Build headers for a request.
 */
function buildHeaders(conn) {
    const headers = {
        'x-ms-version': API_VERSION,
    };
    if (conn.type === 'azure-ad' && conn.accessToken) {
        headers['Authorization'] = `Bearer ${conn.accessToken}`;
    }
    return headers;
}

/**
 * Build the base service URL.
 */
function serviceUrl(conn) {
    if (conn.type === 'sas') {
        return conn.baseUrl;    // may be a proxy
    }
    return `https://${conn.accountName}.blob.core.windows.net`;
}

/**
 * Append SAS token to a URL.
 */
function appendSas(url, conn) {
    if (conn.type === 'sas' && conn.sasToken) {
        const sep = url.includes('?') ? '&' : '?';
        return url + sep + conn.sasToken;
    }
    return url;
}

// ─── Container operations ──────────────────────────────────────

/**
 * List containers in the storage account.
 * @param {StorageConnection} conn
 * @returns {Promise<Array<{name:string, properties:Object}>>}
 */
export async function listContainers(conn) {
    let url = `${serviceUrl(conn)}/?comp=list&include=metadata`;
    url = appendSas(url, conn);

    const resp = await fetch(url, { headers: buildHeaders(conn) });
    if (!resp.ok) throw new Error(`List containers failed: ${resp.status} ${resp.statusText}`);

    const text = await resp.text();
    return parseContainersXml(text);
}

function parseContainersXml(xml) {
    const parser = new DOMParser();
    const doc = parser.parseFromString(xml, 'application/xml');
    const containers = doc.querySelectorAll('Container');
    return Array.from(containers).map(c => ({
        name: c.querySelector('Name')?.textContent ?? '',
        lastModified: c.querySelector('Last-Modified')?.textContent ??
                      c.querySelector('Properties > Last-Modified')?.textContent ?? '',
    }));
}

// ─── Blob operations ───────────────────────────────────────────

/**
 * List blobs (and virtual directories) in a container, optionally under a prefix.
 * Returns { blobs, prefixes, nextMarker }.
 */
export async function listBlobs(conn, container, prefix = '', marker = '') {
    const base = serviceUrl(conn);
    let url = `${base}/${encodeURIComponent(container)}?restype=container&comp=list&delimiter=/&maxresults=500`;
    if (prefix) url += `&prefix=${encodeURIComponent(prefix)}`;
    if (marker) url += `&marker=${encodeURIComponent(marker)}`;
    url = appendSas(url, conn);

    const resp = await fetch(url, { headers: buildHeaders(conn) });
    if (!resp.ok) throw new Error(`List blobs failed: ${resp.status} ${resp.statusText}`);

    const text = await resp.text();
    return parseBlobsXml(text, prefix);
}

function parseBlobsXml(xml, prefix) {
    const parser = new DOMParser();
    const doc = parser.parseFromString(xml, 'application/xml');

    const blobs = Array.from(doc.querySelectorAll('Blob')).map(b => {
        const fullName = b.querySelector('Name')?.textContent ?? '';
        const name = fullName.startsWith(prefix) ? fullName.slice(prefix.length) : fullName;
        return {
            name,
            fullName,
            size: parseInt(b.querySelector('Content-Length')?.textContent ?? '0', 10),
            lastModified: b.querySelector('Last-Modified')?.textContent ?? '',
            contentType: b.querySelector('Content-Type')?.textContent ?? '',
            isFolder: false,
        };
    });

    const prefixes = Array.from(doc.querySelectorAll('BlobPrefix')).map(p => {
        const fullName = p.querySelector('Name')?.textContent ?? '';
        let name = fullName.startsWith(prefix) ? fullName.slice(prefix.length) : fullName;
        if (name.endsWith('/')) name = name.slice(0, -1);
        return {
            name,
            fullName,
            size: 0,
            lastModified: '',
            contentType: '',
            isFolder: true,
        };
    });

    const nextMarker = doc.querySelector('NextMarker')?.textContent ?? '';

    return { blobs, prefixes, nextMarker };
}

/**
 * List ALL blobs under a prefix (recursively, no delimiter) for folder download.
 */
export async function listAllBlobs(conn, container, prefix = '') {
    const all = [];
    let marker = '';
    do {
        const base = serviceUrl(conn);
        let url = `${base}/${encodeURIComponent(container)}?restype=container&comp=list&maxresults=5000`;
        if (prefix) url += `&prefix=${encodeURIComponent(prefix)}`;
        if (marker) url += `&marker=${encodeURIComponent(marker)}`;
        url = appendSas(url, conn);

        const resp = await fetch(url, { headers: buildHeaders(conn) });
        if (!resp.ok) throw new Error(`List all blobs failed: ${resp.status} ${resp.statusText}`);

        const text = await resp.text();
        const parser = new DOMParser();
        const doc = parser.parseFromString(text, 'application/xml');

        const blobs = Array.from(doc.querySelectorAll('Blob')).map(b => ({
            name: b.querySelector('Name')?.textContent ?? '',
            size: parseInt(b.querySelector('Content-Length')?.textContent ?? '0', 10),
            contentType: b.querySelector('Content-Type')?.textContent ?? '',
        }));

        all.push(...blobs);
        marker = doc.querySelector('NextMarker')?.textContent ?? '';
    } while (marker);

    return all;
}

/**
 * Get a download URL for a single blob.
 */
export function getBlobUrl(conn, container, blobName) {
    const base = serviceUrl(conn);
    let url = `${base}/${encodeURIComponent(container)}/${blobName.split('/').map(encodeURIComponent).join('/')}`;
    url = appendSas(url, conn);
    return url;
}

/**
 * Fetch blob content as a Response (for streaming to disk).
 */
export async function fetchBlob(conn, container, blobName) {
    const url = getBlobUrl(conn, container, blobName);
    const resp = await fetch(url, { headers: buildHeaders(conn) });
    if (!resp.ok) throw new Error(`Download failed: ${resp.status} ${resp.statusText}`);
    return resp;
}

// ─── Block list operations ─────────────────────────────────────

/**
 * Get the block list for a blob.
 * @param {StorageConnection} conn
 * @param {string} container
 * @param {string} blobName
 * @param {'committed'|'uncommitted'|'all'} blockListType
 * @returns {Promise<{committedBlocks: Array, uncommittedBlocks: Array}>}
 */
export async function getBlockList(conn, container, blobName, blockListType = 'all') {
    const base = serviceUrl(conn);
    let url = `${base}/${encodeURIComponent(container)}/${blobName.split('/').map(encodeURIComponent).join('/')}`;
    url += `?comp=blocklist&blocklisttype=${blockListType}`;
    url = appendSas(url, conn);

    const resp = await fetch(url, { headers: buildHeaders(conn) });
    if (!resp.ok) throw new Error(`Get block list failed: ${resp.status} ${resp.statusText}`);

    const text = await resp.text();
    return parseBlockListXml(text);
}

function parseBlockListXml(xml) {
    const parser = new DOMParser();
    const doc = parser.parseFromString(xml, 'application/xml');

    function parseBlocks(parentTag) {
        const parent = doc.querySelector(parentTag);
        if (!parent) return [];
        return Array.from(parent.querySelectorAll('Block')).map(b => ({
            name: b.querySelector('Name')?.textContent ?? '',
            size: parseInt(b.querySelector('Size')?.textContent ?? '0', 10),
        }));
    }

    return {
        committedBlocks: parseBlocks('CommittedBlocks'),
        uncommittedBlocks: parseBlocks('UncommittedBlocks'),
    };
}

/**
 * Commit (Put Block List) for a blob.
 * Blocks are sorted by name before committing.
 * @param {StorageConnection} conn
 * @param {string} container
 * @param {string} blobName
 * @param {Array<{name:string}>} blocks — blocks to commit (will be sorted by name)
 * @returns {Promise<void>}
 */
export async function commitBlockList(conn, container, blobName, blocks) {
    const sorted = [...blocks].sort((a, b) => a.name.localeCompare(b.name));

    const xmlParts = ['<?xml version="1.0" encoding="utf-8"?>', '<BlockList>'];
    for (const block of sorted) {
        xmlParts.push(`  <Latest>${escapeXml(block.name)}</Latest>`);
    }
    xmlParts.push('</BlockList>');
    const body = xmlParts.join('\n');

    const base = serviceUrl(conn);
    let url = `${base}/${encodeURIComponent(container)}/${blobName.split('/').map(encodeURIComponent).join('/')}`;
    url += '?comp=blocklist';
    url = appendSas(url, conn);

    const headers = buildHeaders(conn);
    headers['Content-Type'] = 'application/xml';
    headers['Content-Length'] = new Blob([body]).size.toString();

    const resp = await fetch(url, { method: 'PUT', headers, body });
    if (!resp.ok) {
        const errText = await resp.text().catch(() => '');
        throw new Error(`Commit block list failed: ${resp.status} ${resp.statusText}\n${errText}`);
    }
}

function escapeXml(str) {
    return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
              .replace(/"/g, '&quot;').replace(/'/g, '&apos;');
}

/**
 * Build a StorageConnection from credential record.
 */
export function connectionFromCredential(cred) {
    if (cred.type === 'sas') {
        return {
            type: 'sas',
            baseUrl: cred.baseUrl,
            sasToken: cred.sasToken,
            containerName: cred.containerName || '',
            folderPrefix: cred.folderPrefix || '',
        };
    }
    // azure-ad
    return {
        type: 'azure-ad',
        accountName: cred.accountName,
        accessToken: cred.accessToken || '',
    };
}
