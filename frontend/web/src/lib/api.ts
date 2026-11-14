import type { Session } from './types';
let token = '';
export class RequestError extends Error {
  constructor(message: string, public status: number, public code: string, public fields: { field: string; message: string }[] = []) { super(message); }
}
export function setCsrf(value: string) { token = value; }
export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers);
  headers.set('Accept', 'application/json');
  if (options.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  if (options.method && !['GET', 'HEAD'].includes(options.method)) headers.set('X-CSRF-Token', token);
  const response = await fetch(path, { cache: 'no-cache', ...options, headers, credentials: 'same-origin' });
  const body = response.status === 204 ? undefined : await response.json().catch(() => ({}));
  if (!response.ok) throw new RequestError(body.title || 'Не удалось выполнить запрос. Попробуйте ещё раз.', response.status, body.code || 'request_failed', body.errors || []);
  return body as T;
}
export async function session(): Promise<Session> { const data = await api<Session>('/api/session'); setCsrf(data.csrfToken); return data; }
export function send<T>(path: string, body: unknown, method = 'POST', headers?: HeadersInit) { return api<T>(path, { method, body: JSON.stringify(body), headers }); }
