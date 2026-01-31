/**
 * /login - Device Flow Initiation & Polling
 * 
 * GET/POST /login - Starts the GitHub OAuth Device Authorization Grant flow
 * POST /login?device_code=xxx - Polls GitHub for the access token
 */

import { Env, GITHUB_CLIENT_ID, GITHUB_SCOPES, jsonResponse, errorResponse } from './shared';

interface DeviceFlowResponse {
	device_code: string;
	user_code: string;
	verification_uri: string;
	verification_uri_complete?: string;
	expires_in: number;
	interval: number;
}

export async function handleLogin(request: Request, env: Env): Promise<Response> {
	const url = new URL(request.url);
	const deviceCode = url.searchParams.get('device_code');

	// If device_code is provided, poll for token
	if (deviceCode) {
		return await pollForToken(deviceCode);
	}

	// Otherwise, initiate device flow
	return await initiateDeviceFlow();
}

async function initiateDeviceFlow(): Promise<Response> {
	const response = await fetch('https://github.com/login/device/code', {
		method: 'POST',
		headers: {
			'Accept': 'application/json',
			'Content-Type': 'application/x-www-form-urlencoded',
		},
		body: `client_id=${GITHUB_CLIENT_ID}&scope=${GITHUB_SCOPES}`,
	});

	if (!response.ok) {
		const text = await response.text();
		return errorResponse(`GitHub device flow failed: ${text}`, response.status);
	}

	const data: DeviceFlowResponse = await response.json();

	return jsonResponse({
		device_code: data.device_code,
		user_code: data.user_code,
		verification_uri: data.verification_uri,
		verification_uri_complete: data.verification_uri_complete || `${data.verification_uri}?user_code=${data.user_code}`,
		expires_in: data.expires_in,
		expires_at: new Date(Date.now() + data.expires_in * 1000).toISOString(),
		interval: data.interval || 5,
	});
}

async function pollForToken(deviceCode: string): Promise<Response> {
	const response = await fetch('https://github.com/login/oauth/access_token', {
		method: 'POST',
		headers: {
			'Accept': 'application/json',
			'Content-Type': 'application/x-www-form-urlencoded',
		},
		body: `client_id=${GITHUB_CLIENT_ID}&device_code=${deviceCode}&grant_type=urn:ietf:params:oauth:grant-type:device_code`,
	});

	const data = await response.json() as { access_token?: string };
	
	// Only cache successful responses (with access_token) for 5 minutes in browser
	const cacheControl = data.access_token 
		? 'private, max-age=300' 
		: 'no-store';
	
	return jsonResponse(data, 200, { 'Cache-Control': cacheControl });
}
