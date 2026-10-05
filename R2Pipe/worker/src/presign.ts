import { AwsClient } from "aws4fetch";

export interface S3Config {
  accountId: string;
  accessKeyId: string;
  secretAccessKey: string;
  bucket: string;
}

export function s3Endpoint(accountId: string): string {
  return `https://${accountId}.r2.cloudflarestorage.com`;
}

/** Encode an object key for a URL path (keep slashes). */
const encKey = (k: string) => k.split("/").map(encodeURIComponent).join("/");

/**
 * A presigned URL for one object (query-string signing, UNSIGNED-PAYLOAD). Anyone holding the URL can do that single
 * method on that single key until it expires; the secret key never leaves the Worker.
 */
export async function presign(cfg: S3Config, method: "GET" | "PUT" | "DELETE" | "HEAD", key: string, expiresSeconds = 3600, now?: Date): Promise<string> {
  const aws = new AwsClient({ accessKeyId: cfg.accessKeyId, secretAccessKey: cfg.secretAccessKey, service: "s3", region: "auto" });
  const url = new URL(`${s3Endpoint(cfg.accountId)}/${cfg.bucket}/${encKey(key)}`);
  url.searchParams.set("X-Amz-Expires", String(expiresSeconds));
  const signed = await aws.sign(new Request(url, { method }), { aws: { signQuery: true, datetime: now ? amzDate(now) : undefined } });
  return signed.url;
}

export function amzDate(d: Date): string {
  return d.toISOString().replace(/[:-]|\.\d{3}/g, "");
}
