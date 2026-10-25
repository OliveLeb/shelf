import { locks } from 'node:worker_threads'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, expect, it, vi } from 'vite-plus/test'
import { createMemoryHistory } from 'vue-router'
import { api, sessionVersionKey } from './api'
import App from './App.vue'
import { makeRouter } from './router'
import { useSession } from './stores/session'

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
  expect((await api('/account')).value).toMatchObject({ id: 'bob', username: 'Bob' })
})

it('rechecks identity when another tab logs in between account verification and root loading', async () => {
  vi.stubGlobal('navigator', { locks })
  const bob = { ...alice, id: 'bob', username: 'Bob' }
  let cookie = alice
  let firstRead = true
  let release: (() => void) | undefined
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account') {
      if (!firstRead) return Response.json(cookie)
      firstRead = false
      return new Promise<Response>((resolve) => {
        release = () => resolve(Response.json(alice))
      })
    }
    if (path === '/api/auth/web/login') {
      cookie = bob
      return Response.json(bob)
    }
    if (path === '/api/spaces/personal/elements')
      return Response.json([{ ...folder, name: `Dossier privé ${cookie.username}` }])
    throw new Error(`Unexpected request: ${path}`)
  })
  const opening = open()
  await vi.waitFor(() => expect(release).toBeTypeOf('function'))
  const switching = useSession(createPinia()).login('Bob', 'another-account-password')
  await flushPromises()
  release!()
  const { page } = await opening
  await switching
  await vi.waitFor(() => expect(page.text()).toContain('Dossier privé Bob'))
  expect(page.get('header').text()).toContain('Bob')
  expect(page.text()).not.toContain('Alice')
})

it('clears private content on a cross-tab logout and ignores an old verification response', async () => {
  vi.stubGlobal('navigator', { locks })
  let signedOut = false
  let delay = false
  let release: (() => void) | undefined
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account') {
      if (signedOut) return new Response(null, { status: 401 })
      if (!delay) return Response.json(alice)
      return new Promise<Response>((resolve) => {
        release = () => resolve(Response.json(alice))
      })
    }
    if (path === '/api/spaces/personal/elements') return Response.json([folder])
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page, router } = await open()
  expect(page.text()).toContain(folder.name)
  delay = true
  window.dispatchEvent(new Event('focus'))
  await vi.waitFor(() => expect(release).toBeTypeOf('function'))
  // The other tab changes the shared marker; the browser delivers storage asynchronously.
  localStorage.setItem(sessionVersionKey, 'logged-out-in-another-tab')
  window.dispatchEvent(
    new StorageEvent('storage', { key: sessionVersionKey, storageArea: localStorage }),
  )
  signedOut = true
  release!()
  await vi.waitFor(() => expect(page.find('input[name="identifier"]').exists()).toBe(true))
  expect(router.currentRoute.value.path).toBe('/connexion')
  expect(page.text()).not.toContain('Alice')
})

it('does not log out the replacement account when a stale tab acts before its storage event', async () => {
  vi.stubGlobal('navigator', { locks })
  let cookie: typeof alice | null = alice
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account')
      return cookie ? Response.json(cookie) : new Response(null, { status: 401 })
    if (path === '/api/auth/web/login') {
      cookie = { ...alice, id: 'bob', username: 'Bob' }
      return Response.json(cookie)
    }
    if (path === '/api/auth/logout') {
      cookie = null
      return new Response(null, { status: 204 })
    }
    throw new Error(`Unexpected request: ${path}`)
  })
  const oldTab = useSession(createPinia())
  await oldTab.verify()
  await useSession(createPinia()).login('Bob', 'another-account-password')
  await oldTab.logout()
  expect((await api('/account')).value).toMatchObject({ id: 'bob' })
  expect(oldTab.account?.username).toBe('Bob')
  expect(oldTab.status).toBe('authenticated')
})

it.each(['http', 'network'])(
  'allows retrying a failed logout after a %s error',
  async (failure) => {
    let failed = false
    vi.stubGlobal('fetch', async (path: string) => {
      if (path === '/api/account') return Response.json(alice)
      if (path === '/api/spaces/personal/elements') return Response.json([folder])
      if (path === '/api/auth/logout') {
        if (failed) return new Response(null, { status: 204 })
        failed = true
        if (failure === 'network') throw new TypeError('Failed to fetch')
        return new Response(null, { status: 503 })
      }
      throw new Error(`Unexpected request: ${path}`)
    })
    const { page, router } = await open()
    await page
      .findAll('button')
      .find((button) => button.text() === 'Se déconnecter')!
      .trigger('click')
    await flushPromises()
    expect(page.get('[role="alert"]').text()).toContain('La déconnexion n’a pas pu être confirmée')
    expect(page.text()).not.toContain('Alice')
    await page.get('button').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/connexion')
    expect(page.find('input[name="identifier"]').exists()).toBe(true)
  },
)

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

class S3Request extends EventTarget {
  upload = new EventTarget()
  status = 200
  withCredentials = false
  open = vi.fn<(method: string, url: string) => void>()
  setRequestHeader = vi.fn<(name: string, value: string) => void>()
  send(_body: Blob) {
    this.dispatchEvent(new Event('load'))
  }
  abort() {
    this.dispatchEvent(new Event('abort'))
  }
}

async function chooseFile(page: Awaited<ReturnType<typeof open>>['page']) {
  const input = page.get('input[type="file"]')
  Object.defineProperty(input.element, 'files', {
    value: [new File(['bonjour'], 'preuve.txt', { type: 'text/plain' })],
  })
  await input.trigger('change')
  await flushPromises()
}

it('keeps an upload in its original folder across navigation and publishes only after confirmation', async () => {
  const folderA = { ...folder, id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', name: 'Dossier A' }
  let confirm: ((response: Response) => void) | undefined
  let published = false
  let declaration: unknown
  class CheckedS3 extends S3Request {
    override send(body: Blob) {
      expect(body.size).toBe(7)
      expect(this.withCredentials).toBe(false)
      super.send(body)
    }
  }
  vi.stubGlobal('XMLHttpRequest', CheckedS3)
  vi.stubGlobal('fetch', async (path: string, options?: RequestInit) => {
    if (path === '/api/account') return Response.json(alice)
    if (path === '/api/spaces/personal/elements') return Response.json([folderA])
    if (path === `/api/folders/${folderA.id}/elements`)
      return Response.json(published ? [{ ...folder, kind: 'file', name: 'preuve.txt' }] : [])
    if (path === '/api/uploads') {
      declaration = JSON.parse(options!.body as string)
      return Response.json({
        id: 'upload',
        method: 'PUT',
        headers: { 'Content-Type': 'text/plain' },
        partSize: 7,
        parts: [{ number: 1, url: 'https://s3.example.test/signed' }],
      })
    }
    if (path === '/api/uploads/upload/complete')
      return new Promise<Response>((resolve) => {
        confirm = (response) => {
          published = true
          resolve(response)
        }
      })
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page, router } = await open()
  await page.get('a[href*="/dossiers/"]').trigger('click')
  await flushPromises()
  await chooseFile(page)
  expect(declaration).toMatchObject({ parentId: folderA.id, name: 'preuve.txt', size: 7 })
  expect(page.text()).toContain('Finalisation')
  expect(page.find('tbody').exists()).toBe(false)
  window.dispatchEvent(new Event('focus'))
  await flushPromises()
  expect(page.text()).toContain('Finalisation')
  await router.push('/')
  await flushPromises()
  expect(page.text()).toContain('Finalisation')
  confirm!(Response.json({ id: 'new-file' }, { status: 201 }))
  await flushPromises()
  expect(page.text()).toContain('Terminé')
  expect(page.get('tbody').text()).not.toContain('preuve.txt')
  await router.push(`/dossiers/${folderA.id}?nom=Dossier+A`)
  await flushPromises()
  expect(page.get('tbody').text()).toContain('preuve.txt')
})

it.each(['network', 'name_conflict'])(
  'does not report success when confirmation fails: %s',
  async (failure) => {
    let verificationFails = false
    vi.stubGlobal('XMLHttpRequest', S3Request)
    vi.stubGlobal('fetch', async (path: string) => {
      if (path === '/api/account') {
        if (verificationFails) throw new TypeError('Connection lost during verification')
        return Response.json(alice)
      }
      if (path === '/api/spaces/personal/elements') return Response.json([])
      if (path === '/api/uploads')
        return Response.json({
          id: 'upload',
          method: 'PUT',
          headers: { 'Content-Type': 'text/plain' },
          partSize: 7,
          parts: [{ number: 1, url: 'https://s3.example.test/signed' }],
        })
      if (path === '/api/uploads/upload/complete') {
        if (failure === 'network') throw new TypeError('Connection lost')
        return Response.json({ error: failure }, { status: 409 })
      }
      throw new Error(`Unexpected request: ${path}`)
    })
    const { page } = await open()
    await chooseFile(page)
    expect(page.get('aside').text()).toContain('Erreur')
    expect(page.get('aside').text()).toContain('preuve.txt')
    expect(page.get('aside').text()).not.toContain('Terminé')
    expect(page.get('[role="alert"]').text()).toContain(
      failure === 'network' ? 'Publication non confirmée' : 'Aucun fichier n’a été remplacé',
    )
    expect(page.find('tbody').exists()).toBe(false)
    verificationFails = true
    window.dispatchEvent(new Event('focus'))
    await flushPromises()
    expect(page.get('[role="alert"]').text()).toContain('Impossible de vérifier votre session')
    expect(page.find('aside').exists()).toBe(false)
    verificationFails = false
    await page.get('button').trigger('click')
    await flushPromises()
    expect(page.get('aside').text()).toContain('preuve.txt')
    expect(page.get('aside').text()).toContain('Erreur')
  },
)

it('interrupts revoked-session uploads and never carries them into the next account', async () => {
  let revoked = false
  let interrupted = false
  const folderId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
  class PendingS3 extends S3Request {
    override send() {}
    override abort() {
      interrupted = true
      super.abort()
    }
  }
  vi.stubGlobal('XMLHttpRequest', PendingS3)
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account')
      return revoked ? new Response(null, { status: 401 }) : Response.json(alice)
    if (path === '/api/spaces/personal/elements') return Response.json([])
    if (path === `/api/folders/${folderId}/elements`)
      return revoked ? new Response(null, { status: 404 }) : Response.json([])
    if (path === '/api/auth/web/login')
      return Response.json({ ...alice, id: 'bob', username: 'Bob' })
    if (path === '/api/uploads')
      return Response.json({
        id: 'upload',
        method: 'PUT',
        headers: {},
        partSize: 7,
        parts: [{ number: 1, url: 'https://s3.example.test/signed' }],
      })
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page, router } = await open(`/dossiers/${folderId}?nom=Dossier+privé+Alice`)
  await chooseFile(page)
  expect(page.get('aside').text()).toContain('preuve.txt')
  revoked = true
  window.dispatchEvent(new Event('focus'))
  await flushPromises()
  expect(interrupted).toBe(true)
  expect(page.find('aside').exists()).toBe(false)
  await page.get('input[name="identifier"]').setValue('Bob')
  await page.get('input[name="password"]').setValue('another-account-password')
  await page.get('form').trigger('submit')
  await flushPromises()
  expect(page.text()).toContain('Bob')
  expect(page.text()).not.toContain('preuve.txt')
  expect(page.text()).not.toContain('Dossier privé Alice')
  expect(router.currentRoute.value.path).toBe('/')
  expect(page.find('aside').exists()).toBe(false)
})
