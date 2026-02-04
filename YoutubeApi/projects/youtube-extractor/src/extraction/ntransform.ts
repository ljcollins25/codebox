/**
 * N-parameter transform for YouTube video URLs
 * 
 * YouTube throttles downloads if the 'n' parameter isn't transformed
 * using a function found in base.js.
 */

import { throwExtractionFailed } from '../utils/errors';

/**
 * Extract the n-transform function from base.js
 */
export function extractNTransformFunction(baseJs: string): string {
  // Multiple patterns to find the n-transform function
  // The function transforms the 'n' parameter to prevent throttling
  const funcPatterns = [
    // Pattern 1: var b = a.split(""), c = ...
    /\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*\{\s*var\s+b\s*=\s*a\.split\(\s*""\s*\)\s*,\s*c\s*=/,
    // Pattern 2: Enhanced pattern
    /(?:function\s+([a-zA-Z0-9$]+)|([a-zA-Z0-9$]+)\s*=\s*function)\s*\(\s*a\s*\)\s*\{\s*var\s+b\s*=\s*a\.split\(\s*""\s*\)/,
    // Pattern 3: Newer YouTube pattern
    /\b([a-zA-Z0-9$]+)\s*=\s*function\(\s*a\s*\)\s*\{[\s\S]*?var\s+b\s*=\s*a\.split\(\s*""\s*\)[\s\S]*?return\s+b\.join\(\s*""\s*\)\s*\}/,
  ];

  for (const pattern of funcPatterns) {
    const match = baseJs.match(pattern);
    if (match) {
      const funcName = match[1] || match[2];
      return extractFullNFunction(baseJs, funcName);
    }
  }

  // Alternative: look for the 'enhanced_except' pattern used in newer versions
  const enhancedPattern = /&&\(b=a\.get\("n"\)\)&&\(b=([a-zA-Z0-9$]+)(?:\[(\d+)\])?\(b\)/;
  const enhancedMatch = baseJs.match(enhancedPattern);
  
  if (enhancedMatch) {
    let funcName = enhancedMatch[1];
    
    // If there's an array index, need to find the array and get the function name
    if (enhancedMatch[2]) {
      const arrayPattern = new RegExp(
        `var\\s+${escapeRegex(funcName)}\\s*=\\s*\\[([^\\]]+)\\]`
      );
      const arrayMatch = baseJs.match(arrayPattern);
      if (arrayMatch) {
        const items = arrayMatch[1].split(',').map(s => s.trim());
        const index = parseInt(enhancedMatch[2], 10);
        if (items[index]) {
          funcName = items[index];
        }
      }
    }
    
    return extractFullNFunction(baseJs, funcName);
  }

  throwExtractionFailed('Could not find n-transform function');
}

/**
 * Extract the full n-transform function including any helper functions
 */
function extractFullNFunction(baseJs: string, funcName: string): string {
  // Find the full function body
  const funcPattern = new RegExp(
    `(?:var\\s+)?${escapeRegex(funcName)}\\s*=\\s*function\\([^)]*\\)\\s*\\{`,
    'm'
  );
  
  const match = baseJs.match(funcPattern);
  if (!match) {
    // Try alternative pattern for function declaration
    const altPattern = new RegExp(
      `function\\s+${escapeRegex(funcName)}\\s*\\([^)]*\\)\\s*\\{`,
      'm'
    );
    const altMatch = baseJs.match(altPattern);
    if (!altMatch) {
      throwExtractionFailed(`Could not find n-transform function body for: ${funcName}`);
    }
  }

  // Extract the complete function by finding balanced braces
  const startIndex = match ? match.index! + match[0].length - 1 : 0;
  const funcBody = extractFunctionBody(baseJs, startIndex);

  // Build self-contained function
  return `
    var nTransformFunc = function(a) ${funcBody};
    function nTransform(n) {
      return nTransformFunc(n);
    }
  `;
}

/**
 * Extract function body by matching balanced braces
 */
function extractFunctionBody(code: string, startIndex: number): string {
  let depth = 0;
  let inString = false;
  let stringChar = '';
  let escape = false;
  let start = startIndex;

  for (let i = startIndex; i < code.length; i++) {
    const char = code[i];

    if (escape) {
      escape = false;
      continue;
    }

    if (char === '\\' && inString) {
      escape = true;
      continue;
    }

    if ((char === '"' || char === "'" || char === '`') && !inString) {
      inString = true;
      stringChar = char;
      continue;
    }

    if (char === stringChar && inString) {
      inString = false;
      continue;
    }

    if (!inString) {
      if (char === '{') {
        depth++;
      } else if (char === '}') {
        depth--;
        if (depth === 0) {
          return code.substring(start, i + 1);
        }
      }
    }
  }

  throwExtractionFailed('Could not extract n-transform function body');
}

/**
 * Create an executable n-transform function
 */
export function createNTransformExecutor(nTransformCode: string): (n: string) => string {
  try {
    const fn = new Function('n', `
      ${nTransformCode}
      return nTransform(n);
    `);
    return fn as (n: string) => string;
  } catch (e) {
    // N-transform might fail on complex functions, return identity
    console.warn('Failed to create n-transform function:', e);
    return (n: string) => n;
  }
}

/**
 * Apply n-transform to a URL if it has an 'n' parameter
 */
export function applyNTransform(
  url: string,
  nTransformFn: (n: string) => string
): string {
  const urlObj = new URL(url);
  const n = urlObj.searchParams.get('n');
  
  if (n) {
    try {
      const transformedN = nTransformFn(n);
      urlObj.searchParams.set('n', transformedN);
    } catch (e) {
      // If transform fails, leave URL unchanged (may result in throttling)
      console.warn('N-transform failed:', e);
    }
  }
  
  return urlObj.toString();
}

/**
 * Escape special regex characters
 */
function escapeRegex(str: string): string {
  return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
