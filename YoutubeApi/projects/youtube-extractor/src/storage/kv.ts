/**
 * KV Storage layer for tokens and caching
 */

import type { Env } from '../index';

export interface TokenData {
  youtube_cookies: string;
  created_at: string;
  last_used: string;
  label?: string;
}

export interface CachedPlayerFunctions {
  playerVersion: string;
  decipherFunction: string;
  nTransformFunction: string;
  cachedAt: string;
}

const TOKEN_PREFIX = 'tokens/';
const PLAYER_CACHE_PREFIX = 'player/';

/**
 * Generate a cryptographically random token
 */
export function generateToken(): string {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return Array.from(bytes)
    .map(b => b.toString(16).padStart(2, '0'))
    .join('');
}

/**
 * Store a token with associated YouTube cookies
 */
export async function storeToken(
  env: Env,
  token: string,
  cookies: string,
  label?: string
): Promise<void> {
  const data: TokenData = {
    youtube_cookies: cookies,
    created_at: new Date().toISOString(),
    last_used: new Date().toISOString(),
    label,
  };

  await env.TOKENS.put(TOKEN_PREFIX + token, JSON.stringify(data), {
    // Tokens expire after 90 days of inactivity
    expirationTtl: 90 * 24 * 60 * 60,
  });
}

/**
 * Get token data, returns null if not found
 */
export async function getToken(env: Env, token: string): Promise<TokenData | null> {
  const data = await env.TOKENS.get(TOKEN_PREFIX + token);
  if (!data) return null;
  return JSON.parse(data) as TokenData;
}

/**
 * Update token's last_used timestamp and optionally cookies
 */
export async function updateToken(
  env: Env,
  token: string,
  updates: { cookies?: string; last_used?: string }
): Promise<void> {
  const existing = await getToken(env, token);
  if (!existing) return;

  const updated: TokenData = {
    ...existing,
    last_used: updates.last_used || new Date().toISOString(),
    youtube_cookies: updates.cookies || existing.youtube_cookies,
  };

  await env.TOKENS.put(TOKEN_PREFIX + token, JSON.stringify(updated), {
    expirationTtl: 90 * 24 * 60 * 60,
  });
}

/**
 * Delete a token
 */
export async function deleteToken(env: Env, token: string): Promise<void> {
  await env.TOKENS.delete(TOKEN_PREFIX + token);
}

/**
 * Cache extracted player functions
 */
export async function cachePlayerFunctions(
  env: Env,
  playerVersion: string,
  decipherFunction: string,
  nTransformFunction: string
): Promise<void> {
  const data: CachedPlayerFunctions = {
    playerVersion,
    decipherFunction,
    nTransformFunction,
    cachedAt: new Date().toISOString(),
  };

  await env.CACHE.put(PLAYER_CACHE_PREFIX + playerVersion, JSON.stringify(data), {
    // Player functions cached for 7 days
    expirationTtl: 7 * 24 * 60 * 60,
  });
}

/**
 * Get cached player functions
 */
export async function getCachedPlayerFunctions(
  env: Env,
  playerVersion: string
): Promise<CachedPlayerFunctions | null> {
  const data = await env.CACHE.get(PLAYER_CACHE_PREFIX + playerVersion);
  if (!data) return null;
  return JSON.parse(data) as CachedPlayerFunctions;
}
