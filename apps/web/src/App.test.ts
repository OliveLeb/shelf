import { locks } from 'node:worker_threads'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, expect, it, vi } from 'vite-plus/test'
import { createMemoryHistory } from 'vue-router'
import { api } from './api'
import App from './App.vue'
import { makeRouter } from './router'

enableAutoUnmount(afterEach)
afterEach(() => vi.unstubAllGlobals())

const alice = {
  id: 'alice',
  username: 'Alice',
  email: 'alice@example.test',
  isAdmin: false,
  isActive: true,
}
const folder = {
  id: 'private',
  name: 'Dossier privé Alice',
  kind: 'folder',
  size: null,
  addedAt: '2026-10-24T12:00:00Z',
}

async function open(path = '/') {
  const pinia = createPinia()
  const router = makeRouter(pinia, createMemoryHistory())
  await router.push(path)
  const page = mount(App, { global: { plugins: [pinia, router] } })
  await flushPromises()
  return { page, router }
}

it('verifies the server session at startup and removes private content when a refreshed session is rejected', async () => {
  let revoked = false
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      if (path === '/api/account')
        return revoked ? new Response('', { status: 401 }) : Response.json(alice)
      if (path === '/api/spaces/personal/elements') return Response.json([folder])
      throw new Error(`Unexpected request: ${path}`)
    }),
  )
  const { page, router } = await open()
  expect(page.text()).toContain('Dossier privé Alice')
  revoked = true
  window.dispatchEvent(new Event('focus'))
  await flushPromises()
  expect(router.currentRoute.value.path).toBe('/connexion')
  expect(page.find('input[name="identifier"]').exists()).toBe(true)
  expect(page.text()).not.toContain('Dossier privé Alice')
  expect(page.text()).not.toContain('Alice')
})

it('does not restore a late private response after logout or reuse it for the next account', async () => {
  let pendingRoot: ((response: Response) => void) | undefined
  let signedOut = false
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      if (path === '/api/account') return Response.json(alice)
      if (path === '/api/auth/logout') {
        signedOut = true
        return new Response(null, { status: 204 })
      }
      if (path === '/api/auth/web/login')
        return Response.json({ ...alice, id: 'bob', username: 'Bob' })
      if (path === '/api/spaces/personal/elements') {
        if (signedOut) return Response.json([])
        return new Promise<Response>((resolve) => {
          pendingRoot = resolve
        })
      }
      throw new Error(`Unexpected request: ${path}`)
    }),
  )
  const { page } = await open()
  const logout = page.findAll('button').find((button) => button.text() === 'Se déconnecter')!
  await logout.trigger('click')
  await flushPromises()
  await page.get('input[name="identifier"]').setValue('Bob')
  await page.get('input[name="password"]').setValue('another-account-password')
  await page.get('form').trigger('submit')
  await flushPromises()
  pendingRoot!(Response.json([folder]))
  await flushPromises()
  expect(page.text()).toContain('Bob')
  expect(page.text()).toContain('Votre espace est encore vide')
  expect(page.text()).not.toContain('Dossier privé Alice')
})

it('keeps the new account session when an old response renews its cookie during account switching', async () => {
  vi.stubGlobal('navigator', { locks })
  let cookie = alice
  let firstRead = true
  let release: (() => void) | undefined
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      if (path === '/api/auth/web/login') {
        cookie = { ...alice, id: 'bob', username: 'Bob' }
        return Response.json(cookie)
      }
      if (path === '/api/account') {
        if (!firstRead) return Response.json(cookie)
        firstRead = false
        const previous = cookie
        return new Promise<Response>((resolve) => {
          release = () => {
            // A delayed Set-Cookie is applied by the browser before fetch resolves.
            cookie = previous
            resolve(Response.json(previous))
          }
        })
      }
      throw new Error(`Unexpected request: ${path}`)
    }),
  )
  const reading = api('/account')
  await vi.waitFor(() => expect(release).toBeTypeOf('function'))
  const switching = api('/auth/web/login', { method: 'POST' })
  await flushPromises()
  release!()
  await Promise.all([reading, switching])
  expect(await api('/account')).toMatchObject({ id: 'bob', username: 'Bob' })
})

it.each([
  ['/?vue=liste#contenu', '/?vue=liste#contenu'],
  ['https://evil.example/', '/'],
  ['//evil.example/', '/'],
  ['/\\evil.example/', '/'],
  ['/admin/accounts', '/'],
])('resumes only an implemented authorized internal destination: %s', async (retour, expected) => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string) => {
      if (path === '/api/account') return Response.json(alice)
      if (path === '/api/spaces/personal/elements') return Response.json([])
      throw new Error(`Unexpected request: ${path}`)
    }),
  )
  const { router } = await open(`/connexion?retour=${encodeURIComponent(retour)}`)
  expect(router.currentRoute.value.fullPath).toBe(expected)
})
