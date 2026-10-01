import type { ApiProblem } from '../generated/generated';

/** Error for a non-2xx response. `code` is the ProblemDetails `code` (e.g. `InvalidCredentials`) when present. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | null;
  constructor(status: number, message: string, code: string | null = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

/**
 * Same-origin JSON fetch. Sends the `X-X4MP` CSRF header on every non-GET request (server-design 4.2), maps
 * ProblemDetails bodies to ApiError, and returns undefined for 204.
 */
export async function http<T>(path: string, init: RequestInit = {}): Promise<T> {
  const method = (init.method ?? 'GET').toUpperCase();
  const headers = new Headers(init.headers);
  if (method !== 'GET' && method !== 'HEAD') headers.set('X-X4MP', '1');
  if (init.body !== undefined && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  const res = await fetch(path, { credentials: 'same-origin', ...init, headers });
  if (!res.ok) {
    let problem: Partial<ApiProblem> | null = null;
    try {
      problem = (await res.json()) as Partial<ApiProblem>;
    } catch {
      // not JSON
    }
    throw new ApiError(res.status, problem?.detail ?? problem?.title ?? res.statusText, problem?.code ?? null);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export function postJson<T>(path: string, body?: unknown): Promise<T> {
  return http<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });
}
