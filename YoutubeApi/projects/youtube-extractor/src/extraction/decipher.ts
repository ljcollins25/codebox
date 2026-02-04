/**
 * Signature deciphering for YouTube video URLs
 * 
 * YouTube obfuscates video URLs with a signature that must be deciphered
 * using a function found in base.js (the player JavaScript file).
 */

import { throwExtractionFailed } from '../utils/errors';

/**
 * Extract the signature decipher function from base.js
 */
export function extractDecipherFunction(baseJs: string): string {
  // Find the main decipher function
  // Pattern: var Xo = function(a) { a = a.split(""); ... return a.join("") };
  const funcPatterns = [
    /\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*\{\s*a\s*=\s*a\.split\(\s*""\s*\)([\s\S]*?)return\s+a\.join\(\s*""\s*\)\s*\}/,
    /function\s+([a-zA-Z0-9$]+)\(\s*a\s*\)\s*\{\s*a\s*=\s*a\.split\(\s*""\s*\)([\s\S]*?)return\s+a\.join\(\s*""\s*\)\s*\}/,
  ];

  let funcName: string | null = null;
  let funcBody: string | null = null;

  for (const pattern of funcPatterns) {
    const match = baseJs.match(pattern);
    if (match) {
      funcName = match[1];
      funcBody = match[0];
      break;
    }
  }

  if (!funcName || !funcBody) {
    throwExtractionFailed('Could not find decipher function');
  }

  // Find the helper object used by the function
  // Pattern: var Wo = { Vh: function(a, b) { ... }, Gc: function(a) { ... } };
  const helperMatch = funcBody.match(/([a-zA-Z0-9$]+)\.[a-zA-Z0-9$]+\(/);
  if (!helperMatch) {
    // Return just the function if no helper found
    return buildDecipherFunction(funcBody, '');
  }

  const helperName = helperMatch[1];
  const helperPattern = new RegExp(
    `var\\s+${escapeRegex(helperName)}\\s*=\\s*\\{[\\s\\S]*?\\};`,
    'm'
  );
  const helperObjMatch = baseJs.match(helperPattern);

  const helperCode = helperObjMatch ? helperObjMatch[0] : '';

  return buildDecipherFunction(funcBody, helperCode);
}

/**
 * Build a self-contained decipher function
 */
function buildDecipherFunction(mainFunc: string, helperCode: string): string {
  // Create a wrapper function that takes a signature and returns deciphered
  return `
    ${helperCode}
    var decipherFunc = ${mainFunc};
    function decipher(sig) {
      return decipherFunc(sig);
    }
  `;
}

/**
 * Create an executable decipher function
 */
export function createDecipherExecutor(decipherCode: string): (sig: string) => string {
  try {
    const fn = new Function('sig', `
      ${decipherCode}
      return decipher(sig);
    `);
    return fn as (sig: string) => string;
  } catch (e) {
    throwExtractionFailed(`Failed to create decipher function: ${e}`);
  }
}

/**
 * Parse signature cipher string and decipher the URL
 */
export function decipherSignatureCipher(
  signatureCipher: string,
  decipherFn: (sig: string) => string
): string {
  const params = new URLSearchParams(signatureCipher);
  
  const encryptedSig = params.get('s');
  const sigParam = params.get('sp') || 'signature';
  const baseUrl = params.get('url');

  if (!encryptedSig || !baseUrl) {
    throwExtractionFailed('Invalid signature cipher format');
  }

  const decipheredSig = decipherFn(encryptedSig);
  
  // Add the deciphered signature to the URL
  const url = new URL(baseUrl);
  url.searchParams.set(sigParam, decipheredSig);
  
  return url.toString();
}

/**
 * Escape special regex characters
 */
function escapeRegex(str: string): string {
  return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
