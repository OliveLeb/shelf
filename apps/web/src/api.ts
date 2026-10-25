export class ApiError extends Error {
  constructor(
    public status: number,
    public version: string | null,
    options?: ErrorOptions,
    public code?: string,
  ) {
    super(`HTTP ${status}`, options)
  }
}

export class SessionChangedError extends Error {}

// An invalidation marker, never an account identifier or an authentication credential.
export const sessionVersionKey = 'shelf-session-version'

// The browser alone owns the HttpOnly session cookie. Never accept a token here.
export async function api<T>(
  path: string,
  options: RequestInit = {},
  expectedVersion?: string | null,
): Promise<{ value: T; version: string | null }> {
  const changesSession = path === '/auth/web/login' || path === '/auth/logout'
  async function send() {
    let version = localStorage.getItem(sessionVersionKey)
    // Check after acquiring the lock: a queued login may have replaced the cookie.
    if (expectedVersion !== undefined && expectedVersion !== version)
      throw new SessionChangedError('Session changed')
    if (changesSession) {
      version = crypto.randomUUID()
      // Invalidate even on failure: a rejected login can also clear the cookie.
      localStorage.setItem(sessionVersionKey, version)
    }
    try {
      const response = await fetch(`/api${path}`, {
        ...options,
        credentials: 'same-origin',
        cache: 'no-store',
        headers: {
          ...(options.body ? { 'Content-Type': 'application/json' } : {}),
          'X-Shelf-CSRF': '1',
          ...options.headers,
        },
      })
      if (!response.ok) {
        const body = await response.json().catch(() => null)
        throw new ApiError(response.status, version, undefined, body?.error)
      }
      const value: T = response.status === 204 ? undefined : await response.json()
      return { value, version }
    } catch (error) {
      if (error instanceof ApiError) throw error
      throw new ApiError(0, version, { cause: error })
    }
  }

  // Cookie response headers bypass Pinia's generation guard. Across tabs, finish
  // old requests before login/logout can replace the profile's shared cookie.
  return navigator.locks
    ? navigator.locks.request(
        'shelf-session',
        { mode: changesSession ? 'exclusive' : 'shared', signal: options.signal ?? undefined },
        send,
      )
    : send()
}
