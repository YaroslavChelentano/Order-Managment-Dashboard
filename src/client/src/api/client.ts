const base = '';

export async function apiGet<T>(path: string): Promise<T> {
  const r = await fetch(`${base}${path}`, { headers: { Accept: 'application/json' } });
  const text = await r.text();
  if (!r.ok) throw new Error(text || r.statusText);
  return JSON.parse(text) as T;
}

export async function apiPatch<T>(path: string, body: unknown): Promise<T> {
  const r = await fetch(`${base}${path}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await r.text();
  if (!r.ok) throw new Error(text || r.statusText);
  return JSON.parse(text) as T;
}

export async function apiPost<T>(path: string, body: unknown): Promise<{ status: number; data: T }> {
  const r = await fetch(`${base}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await r.text();
  const data = text ? (JSON.parse(text) as T) : ({} as T);
  return { status: r.status, data };
}
