export interface CanonicalizationResult {
  canonical: string;
  bodySha256: string;
  signedHeaders: string[];
}

export interface CanonicalizationOptions {
  signedHeaders: string[];
  baseUrl?: string;
}

export type HeaderCollection =
  | Headers
  | Record<string, string | readonly string[]>
  | Iterable<readonly [string, string]>;

export interface CanonicalRequest {
  method: string;
  url: string;
  headers?: HeaderCollection;
  body?: unknown;
}

const textEncoder = new TextEncoder();

export async function canonicalizeRequest(
  request: CanonicalRequest,
  options: CanonicalizationOptions
): Promise<CanonicalizationResult> {
  const url = new URL(request.url, options.baseUrl ?? 'https://canonical.invalid');
  const headers = toHeaderMap(request.headers);

  const method = request.method.toUpperCase();
  const path = normalizePath(url.pathname);
  const query = normalizeSearchParams(url.searchParams);
  const normalizedHeaders = normalizeHeaders(headers, options.signedHeaders);

  const contentType = firstHeaderValue(headers, 'content-type');
  const canonicalBodyBytes = await canonicalizeBody(request.body, contentType);
  const bodySha256 = await sha256Base64Url(canonicalBodyBytes);

  const lines: string[] = [
    'REQSIG-V1',
    `METHOD:${method}`,
    `PATH:${path}`,
    `QUERY:${query}`,
    ...normalizedHeaders.headerLines.map(header => `HEADER:${header}`),
    `SIGNED-HEADERS:${normalizedHeaders.signedHeaders.join(';')}`,
    `BODY-SHA256:${bodySha256}`
  ];

  return {
    canonical: lines.join('\n'),
    bodySha256,
    signedHeaders: normalizedHeaders.signedHeaders
  };
}

function normalizePath(pathname: string): string {
  if (!pathname) {
    return '/';
  }

  return pathname.startsWith('/') ? pathname : `/${pathname}`;
}

function normalizeSearchParams(params: URLSearchParams): string {
  const pairs: Array<[string, string]> = [];

  for (const [key, value] of params.entries()) {
    pairs.push([key, value]);
  }

  pairs.sort(compareStringPair);

  return pairs
    .map(([key, value]) => `${rfc3986Encode(key)}=${rfc3986Encode(value)}`)
    .join('&');
}

function normalizeHeaders(
  headers: Map<string, string[]>,
  configuredHeaders: string[]
): { signedHeaders: string[]; headerLines: string[] } {
  const requested = Array.from(
    new Set(configuredHeaders.map(header => header.trim().toLowerCase()).filter(Boolean))
  ).sort();

  const signedHeaders: string[] = [];
  const headerLines: string[] = [];

  for (const name of requested) {
    const values = headers.get(name) ?? [];
    if (values.length === 0) {
      continue;
    }

    const normalizedValues = values
      .map(normalizeHeaderValue)
      .filter(value => value.length > 0)
      .sort();

    if (normalizedValues.length === 0) {
      continue;
    }

    signedHeaders.push(name);
    headerLines.push(`${name}:${normalizedValues.join(',')}`);
  }

  return { signedHeaders, headerLines };
}

function normalizeHeaderValue(value: string): string {
  return value.trim().replace(/\s+/g, ' ');
}

function firstHeaderValue(headers: Map<string, string[]>, name: string): string | null {
  const values = headers.get(name.toLowerCase());
  return values && values.length > 0 ? values[0] : null;
}

async function canonicalizeBody(
  body: unknown,
  contentType: string | null
): Promise<Uint8Array> {
  if (body === null || body === undefined) {
    return new Uint8Array();
  }

  if (body instanceof ArrayBuffer) {
    return new Uint8Array(body);
  }

  if (ArrayBuffer.isView(body)) {
    return new Uint8Array(body.buffer, body.byteOffset, body.byteLength);
  }

  if (typeof Blob !== 'undefined' && body instanceof Blob) {
    return new Uint8Array(await body.arrayBuffer());
  }

  if (typeof FormData !== 'undefined' && body instanceof FormData) {
    throw new Error('FormData / multipart is not supported by canonicalization V1.');
  }

  if (typeof URLSearchParams !== 'undefined' && body instanceof URLSearchParams) {
    const pairs: Array<[string, string]> = [];
    for (const [key, value] of body.entries()) {
      pairs.push([key, value]);
    }

    return textEncoder.encode(normalizeUrlEncodedPairs(pairs));
  }

  if (typeof body === 'string') {
    if (isJsonContentType(contentType)) {
      return textEncoder.encode(canonicalizeJsonString(body));
    }

    if (isFormUrlEncodedContentType(contentType)) {
      const params = new URLSearchParams(body);
      const pairs: Array<[string, string]> = [];

      for (const [key, value] of params.entries()) {
        pairs.push([key, value]);
      }

      return textEncoder.encode(normalizeUrlEncodedPairs(pairs));
    }

    return textEncoder.encode(body);
  }

  if (isUrlEncodedPairs(body)) {
    return textEncoder.encode(normalizeUrlEncodedPairs(Array.from(body)));
  }

  if (typeof body === 'object' || typeof body === 'number' || typeof body === 'boolean') {
    return textEncoder.encode(canonicalJsonStringify(body));
  }

  throw new Error(`Unsupported request body type for canonicalization: ${typeof body}`);
}

function isJsonContentType(contentType: string | null): boolean {
  if (!contentType) {
    return false;
  }

  const lowered = contentType.toLowerCase();
  return lowered.includes('application/json') || lowered.includes('+json');
}

function isFormUrlEncodedContentType(contentType: string | null): boolean {
  if (!contentType) {
    return false;
  }

  return contentType.toLowerCase().includes('application/x-www-form-urlencoded');
}

function canonicalizeJsonString(input: string): string {
  const parsed = JSON.parse(input);
  return canonicalJsonStringify(parsed);
}

function canonicalJsonStringify(value: unknown): string {
  return JSON.stringify(sortJsonValue(value));
}

function sortJsonValue(value: unknown): unknown {
  if (value === null) {
    return null;
  }

  if (Array.isArray(value)) {
    return value.map(sortJsonValue);
  }

  if (typeof value === 'object') {
    const objectValue = value as Record<string, unknown>;
    const sortedKeys = Object.keys(objectValue).sort();
    const result: Record<string, unknown> = {};

    for (const key of sortedKeys) {
      result[key] = sortJsonValue(objectValue[key]);
    }

    return result;
  }

  return value;
}

function normalizeUrlEncodedPairs(pairs: Iterable<readonly [string, string]>): string {
  return Array.from(pairs)
    .map(([key, value]) => [key, value] as [string, string])
    .sort(compareStringPair)
    .map(([key, value]) => `${rfc3986Encode(key)}=${rfc3986Encode(value)}`)
    .join('&');
}

function compareStringPair(a: [string, string], b: [string, string]): number {
  const keyCompare = a[0].localeCompare(b[0], 'en', { sensitivity: 'variant' });
  if (keyCompare !== 0) {
    return keyCompare;
  }

  return a[1].localeCompare(b[1], 'en', { sensitivity: 'variant' });
}

function rfc3986Encode(input: string): string {
  return encodeURIComponent(input)
    .replace(/[!'()*]/g, char => `%${char.charCodeAt(0).toString(16).toUpperCase()}`);
}

async function sha256Base64Url(data: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', data);
  return base64UrlEncode(new Uint8Array(digest));
}

function base64UrlEncode(bytes: Uint8Array): string {
  return Buffer.from(bytes)
    .toString('base64')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/g, '');
}

function toHeaderMap(headers: HeaderCollection | undefined): Map<string, string[]> {
  const map = new Map<string, string[]>();

  if (!headers) {
    return map;
  }

  if (headers instanceof Headers) {
    for (const [name, value] of headers.entries()) {
      pushHeaderValue(map, name, value);
    }

    return map;
  }

  if (Symbol.iterator in Object(headers)) {
    for (const [name, value] of headers as Iterable<readonly [string, string]>) {
      pushHeaderValue(map, name, value);
    }

    return map;
  }

  for (const [name, value] of Object.entries(headers)) {
    const values = Array.isArray(value) ? value : [value];
    for (const item of values) {
      pushHeaderValue(map, name, item);
    }
  }

  return map;
}

function pushHeaderValue(map: Map<string, string[]>, name: string, value: string): void {
  const key = name.toLowerCase();
  const existing = map.get(key);
  if (existing) {
    existing.push(value);
    return;
  }

  map.set(key, [value]);
}

function isUrlEncodedPairs(value: unknown): value is Iterable<readonly [string, string]> {
  if (!value || typeof value !== 'object' || !(Symbol.iterator in value)) {
    return false;
  }

  for (const entry of value as Iterable<unknown>) {
    return Array.isArray(entry) && entry.length === 2;
  }

  return true;
}
