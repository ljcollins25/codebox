/**
 * auth.js — Authentication helpers for Azure AD (MSAL) and SAS URL connections.
 */

// MSAL configuration for Azure AD authentication.
// Users provide their own Azure AD app registration client ID.
const DEFAULT_AUTHORITY = 'https://login.microsoftonline.com/common';
const STORAGE_SCOPE = 'https://storage.azure.com/.default';

let msalInstance = null;

/**
 * Initialise (or reinitialise) the MSAL instance for a given client ID / tenant.
 */
export function initMsal(clientId, tenantId) {
    const authority = tenantId
        ? `https://login.microsoftonline.com/${tenantId}`
        : DEFAULT_AUTHORITY;

    const config = {
        auth: {
            clientId,
            authority,
            redirectUri: window.location.origin + window.location.pathname,
        },
        cache: {
            cacheLocation: 'localStorage',
            storeAuthStateInCookie: false,
        },
    };

    // msal is loaded from CDN onto window
    if (typeof msal === 'undefined') {
        throw new Error('MSAL library not loaded. Check your internet connection.');
    }
    msalInstance = new msal.PublicClientApplication(config);
    return msalInstance;
}

/**
 * Interactive login popup — returns an access token for Azure Storage.
 */
export async function loginPopup(clientId, tenantId) {
    const app = initMsal(clientId, tenantId);
    await app.initialize();

    const loginResponse = await app.loginPopup({
        scopes: [STORAGE_SCOPE],
    });

    // Acquire token silently with the logged-in account
    const tokenResponse = await app.acquireTokenSilent({
        scopes: [STORAGE_SCOPE],
        account: loginResponse.account,
    });

    return {
        accessToken: tokenResponse.accessToken,
        account: loginResponse.account,
        expiresOn: tokenResponse.expiresOn,
    };
}

/**
 * Acquire a fresh token silently (for already-authenticated sessions).
 */
export async function acquireTokenSilent(clientId, tenantId, accountHomeId) {
    if (!msalInstance) initMsal(clientId, tenantId);
    await msalInstance.initialize();

    const accounts = msalInstance.getAllAccounts();
    const account = accounts.find(a => a.homeAccountId === accountHomeId) || accounts[0];
    if (!account) throw new Error('No cached account. Please log in again.');

    const tokenResponse = await msalInstance.acquireTokenSilent({
        scopes: [STORAGE_SCOPE],
        account,
    });

    return {
        accessToken: tokenResponse.accessToken,
        expiresOn: tokenResponse.expiresOn,
    };
}

/**
 * Parse a SAS URL into its components.
 * Supports non-standard hostnames (proxy sites).
 *
 * Expected shapes:
 *   https://host.com/?sv=...                  → account-level
 *   https://host.com/container?sv=...         → container-level
 *   https://host.com/container/folder/?sv=...  → folder-level
 *   https://myaccount.blob.core.windows.net/container?sv=... → standard
 */
export function parseSasUrl(rawUrl) {
    const url = new URL(rawUrl.trim());
    const sasToken = url.search.startsWith('?') ? url.search.slice(1) : url.search;
    const baseUrl = `${url.protocol}//${url.host}`;

    // Split pathname into parts — first non-empty segment is container
    const parts = url.pathname.split('/').filter(Boolean);
    const containerName = parts[0] || '';
    const folderPrefix = parts.length > 1 ? parts.slice(1).join('/') + '/' : '';

    // Try to derive account name from hostname (standard Azure URLs).
    let accountName = '';
    const hostParts = url.hostname.split('.');
    if (hostParts.length >= 3 && url.hostname.endsWith('.blob.core.windows.net')) {
        accountName = hostParts[0];
    }

    return {
        rawUrl: rawUrl.trim(),
        baseUrl,
        accountName,
        containerName,
        folderPrefix,
        sasToken,
        host: url.host,
    };
}
