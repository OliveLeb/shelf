export class ApiError extends Error {
  constructor(public status: number) {
    super(`HTTP ${status}`)
  }
}

// The browser alone owns the HttpOnly session cookie. Never accept a token here.
export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  async function send(): Promise<T> {
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
    if (!response.ok) throw new ApiError(response.status)
    return response.status === 204 ? (undefined as T) : response.json()
  }

  // Cookie response headers bypass Pinia's generation guard. Across tabs, finish
  // old requests before login/logout can replace the profile's shared cookie.
  const changesSession = path === '/auth/web/login' || path === '/auth/logout'
  return navigator.locks
    ? navigator.locks.request(
        'shelf-session',
        { mode: changesSession ? 'exclusive' : 'shared', signal: options.signal ?? undefined },
        send,
      )
    : send()
}
