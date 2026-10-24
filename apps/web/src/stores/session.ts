import { defineStore } from 'pinia'
import { shallowRef } from 'vue'
import { api, ApiError } from '../api'

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
  let generation = 0
  let verification: Promise<void> | undefined

  function clear(next: typeof status.value) {
    generation++
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
    clear('checking')
    const current = generation
    verification = (async () => {
      try {
        const value = await api<Account>('/account')
        if (current === generation) accept(value)
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

  async function login(identifier: string, password: string) {
    const value = await api<Account>('/auth/web/login', {
      method: 'POST',
      body: JSON.stringify({ identifier, password, deviceName: 'Navigateur web' }),
    })
    clear('anonymous')
    logoutFailed.value = false
    accept(value)
  }

  async function logout() {
    clear('checking')
    try {
      await api<void>('/auth/logout', { method: 'POST' })
      logoutFailed.value = false
      clear('anonymous')
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        logoutFailed.value = false
        clear('anonymous')
      } else {
        logoutFailed.value = true
        clear('error')
      }
    }
  }

  async function request<T>(path: string, options?: RequestInit) {
    const current = generation
    try {
      const result = await api<T>(path, options)
      if (current !== generation) throw new DOMException('Session changed', 'AbortError')
      return result
    } catch (error) {
      if (current === generation && error instanceof ApiError && error.status === 401)
        clear('anonymous')
      throw error
    }
  }

  return { account, status, logoutFailed, verify, login, logout, request }
})
