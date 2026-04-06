const BASE_URL = process.env.API_URL || 'http://localhost:3000';

export interface ApiResponse<T = unknown> {
  status: number;
  data: T;
  headers: Headers;
  ok: boolean;
  responseTime: number;
}

export async function api<T = unknown>(
  method: string,
  path: string,
  body?: unknown
): Promise<ApiResponse<T>> {
  const url = `${BASE_URL}${path}`;
  const start = performance.now();

  const res = await fetch(url, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });

  const responseTime = performance.now() - start;
  const contentType = res.headers.get('content-type') || '';
  let data: T;

  if (contentType.includes('application/json')) {
    data = (await res.json()) as T;
  } else {
    data = (await res.text()) as unknown as T;
  }

  return { status: res.status, data, headers: res.headers, ok: res.ok, responseTime };
}

export const get = <T = unknown>(path: string) => api<T>('GET', path);
export const post = <T = unknown>(path: string, body?: unknown) => api<T>('POST', path, body);
export const patch = <T = unknown>(path: string, body?: unknown) => api<T>('PATCH', path, body);
export const del = <T = unknown>(path: string) => api<T>('DELETE', path);

export function getBaseUrl(): string {
  return BASE_URL;
}
