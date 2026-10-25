import { defineStore } from 'pinia'
import { shallowRef } from 'vue'
import { api, ApiError, SessionChangedError } from '../api'

export interface Account {
  id: string
  username: string
  email: string
  isAdmin: boolean
  isActive: boolean
}

export const useSession = defineStore('session', () => {
  const account = shallowRef<Account | null>(null)
  const status = shallowRef<'checking' | 'authenticated' | 'anonymous' | 'error'>('checking')
  const logoutFailed = shallowRef(false)
  const privacyEpoch = shallowRef(0)
  let generation = 0
  let version: string | null = null
  let verification: Promise<void> | undefined

  function clear(next: typeof status.value, preserveWork = false) {
    generation++
    if (!preserveWork) privacyEpoch.value++
    account.value = null
    status.value = next
  }

  function accept(value: Account) {
    // Explicitly retain only public account fields, never a session secret.
    const { id, username, email, isAdmin, isActive } = value
    account.value = { id, username, email, isAdmin, isActive }
    status.value = 'authenticated'
  }

  function verify() {
    if (verification) return verification
    const previousId = account.value?.id
    clear('checking', true)
    const current = generation
    verification = (async () => {
      try {
        const result = await api<Account>('/account')
        if (current === generation) {
          // A focus check may unmount pages, but does not end same-session work.
          if ((previousId && previousId !== result.value.id) || version !== result.version)
            privacyEpoch.value++
          version = result.version
          accept(result.value)
        }
      } catch (error) {
        if (current === generation) {
          clear(error instanceof ApiError && error.status === 401 ? 'anonymous' : 'error')
        }
      } finally {
        verification = undefined
      }
    })()
    return verification
  }

  async function invalidate() {
    clear('checking')
    // A verification already in flight belongs to the old generation.
    await verification
    await verify()
  }

  async function login(identifier: string, password: string) {
    const result = await api<Account>('/auth/web/login', {
      method: 'POST',
      body: JSON.stringify({ identifier, password, deviceName: 'Navigateur web' }),
    })
    clear('anonymous')
    logoutFailed.value = false
    version = result.version
    accept(result.value)
  }

  async function logout() {
    clear('checking')
    try {
      await api<void>('/auth/logout', { method: 'POST' }, version)
      logoutFailed.value = false
      clear('anonymous')
    } catch (error) {
      if (error instanceof SessionChangedError) {
        await invalidate()
      } else if (error instanceof ApiError && error.status === 401) {
        logoutFailed.value = false
        clear('anonymous')
      } else {
        // Retry this logout's generation, never a replacement from another tab.
        if (error instanceof ApiError) version = error.version
        logoutFailed.value = true
        clear('error')
      }
    }
  }

  async function request<T>(path: string, options?: RequestInit) {
    const current = privacyEpoch.value
    try {
      const result = await api<T>(path, options, version)
      if (current !== privacyEpoch.value) throw new DOMException('Session changed', 'AbortError')
      return result.value
    } catch (error) {
      if (current === privacyEpoch.value) {
        if (error instanceof SessionChangedError) await invalidate()
        else if (error instanceof ApiError && error.status === 401) clear('anonymous')
      }
      throw error
    }
  }

  return { account, status, logoutFailed, privacyEpoch, verify, invalidate, login, logout, request }
})
