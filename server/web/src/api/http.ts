import type { ApiProblem } from '../generated/generated';

/**
 * Error for a non-2xx response (the body is an RFC 7807 problem). `code` is the ProblemDetails `code` (e.g. `InvalidCredentials`) when
 * present; `errors` holds the per-field (or per-setting-key) message lists of a `ValidationFailed` answer and `errorCodes` the
 * matching machine-readable code per field.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | null;
  readonly errors: Record<string, string[]> | null;
  readonly errorCodes: Record<string, string> | null;
  constructor(
    status: number,
    message: string,
    code: string | null = null,
    errors: Record<string, string[]> | null = null,
    errorCodes: Record<string, string> | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.errors = errors;
    this.errorCodes = errorCodes;
  }
}

type UnauthorizedListener = () => void;
const unauthorizedListeners = new Set<UnauthorizedListener>();

/**
 * Called when a request outside the login/me/logout calls answers 401, i.e. the session cookie expired or was invalidated
 * (password change elsewhere). The auth provider uses it to send the user back to the login page with a notice.
 */
export function onUnauthorized(listener: UnauthorizedListener): () => void {
  unauthorizedListeners.add(listener);
  return () => {
    unauthorizedListeners.delete(listener);
  };
}

/** Auth probes legitimately answer 401 (not signed in / bad credentials), which is not a session expiry. */
function isAuthProbe(path: string): boolean {
  return /^\/api\/v1\/auth\/(login|me|logout)(\?|$)/.test(path);
}

/**
 * Same-origin JSON fetch. Sends the `X-X4MP` CSRF header on every request (server-design 4.2; harmless on GET and it
 * means no call site can forget it), maps ProblemDetails bodies to ApiError, and returns undefined for 204.
 */
export async function http<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set('X-X4MP', '1');
  if (init.body !== undefined && init.body !== null && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  const res = await fetch(path, { credentials: 'same-origin', ...init, headers });
  if (!res.ok) {
    let problem: Partial<ApiProblem> | null = null;
    try {
      problem = (await res.json()) as Partial<ApiProblem>;
    } catch {
      // not JSON
    }
    if (res.status === 401 && !isAuthProbe(path)) unauthorizedListeners.forEach((l) => l());
    throw new ApiError(
      res.status,
      problem?.detail ?? problem?.title ?? res.statusText,
      problem?.code ?? null,
      problem?.errors ?? null,
      problem?.errorCodes ?? null,
    );
  }
  if (res.status === 204) return undefined as T;
  // 202 Accepted answers carry no body (kick, start, stop...), so an empty body is not an error.
  const text = await res.text();
  return (text === '' ? undefined : JSON.parse(text)) as T;
}

function withBody(method: string, body: unknown): RequestInit {
  return { method, body: body === undefined ? undefined : JSON.stringify(body) };
}

/** Typed verbs over `http`. Paths are absolute (`/api/v1/...`). */
export const api = {
  get: <T>(path: string) => http<T>(path),
  post: <T = void>(path: string, body?: unknown) => http<T>(path, withBody('POST', body)),
  put: <T = void>(path: string, body?: unknown) => http<T>(path, withBody('PUT', body)),
  patch: <T = void>(path: string, body?: unknown) => http<T>(path, withBody('PATCH', body)),
  delete: <T = void>(path: string) => http<T>(path, { method: 'DELETE' }),
};

export function postJson<T>(path: string, body?: unknown): Promise<T> {
  return api.post<T>(path, body);
}
