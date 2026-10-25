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
  const page = mount(App, { attachTo: document.body, global: { plugins: [pinia, router] } })
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

it.each([false, true])(
  'keeps a created account distinct from failed automatic login (previous session: %s)',
  async (signedIn) => {
    let created = false
    vi.stubGlobal('fetch', async (path: string) => {
      if (path === '/api/account')
        return signedIn
          ? Response.json({ ...alice, isAdmin: true })
          : new Response(null, { status: 401 })
      if (path === '/api/auth/web/register') {
        if (created) throw new Error('Registration must not be replayed')
        created = true
        return Response.json(alice, { status: 201 })
      }
      if (path === '/api/auth/web/login') return new Response(null, { status: 503 })
      throw new Error(`Unexpected request: ${path}`)
    })
    const { page, router } = await open('/accept-invitation#token=' + 'A'.repeat(64))
    await page.get('input[name="username"]').setValue('Alice')
    await page.get('input[name="password"]').setValue('a-long-private-password')
    await page.get('form').trigger('submit')
    await flushPromises()
    expect(created).toBe(true)
    expect(page.text()).toContain('Votre compte est créé')
    expect(page.find('form').exists()).toBe(false)
    expect(page.get('a').attributes('href')).toBe('/connexion')
    expect(router.currentRoute.value.hash).toBe('')
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain('A'.repeat(64))
    await router.push('/connexion')
    await flushPromises()
    expect(page.find('input[name="identifier"]').exists()).toBe(true)
  },
)

it('accepts an invitation after a session refresh and enters the new ordinary account’s personal space', async () => {
  let current = { ...alice, isAdmin: true }
  const secret = 'B'.repeat(64)
  let registration: unknown
  vi.stubGlobal('fetch', async (path: string, options?: RequestInit) => {
    if (path === '/api/account') return Response.json(current)
    if (path === '/api/auth/web/register') {
      registration = JSON.parse(String(options?.body))
      return Response.json({ ...alice, id: 'camille', username: 'Camille' }, { status: 201 })
    }
    if (path === '/api/auth/web/login') {
      current = { ...alice, id: 'camille', username: 'Camille' }
      return Response.json(current)
    }
    if (path === '/api/spaces/personal/elements') return Response.json([])
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page, router } = await open('/accept-invitation#token=' + secret)
  await page.get('input[name="username"]').setValue('Camille')
  window.dispatchEvent(new Event('focus'))
  await flushPromises()
  expect((page.get('input[name="username"]').element as HTMLInputElement).value).toBe('Camille')
  await page.get('input[name="password"]').setValue('a-long-private-password')
  await page.get('form').trigger('submit')
  await flushPromises()
  expect(registration).toEqual({
    token: secret,
    username: 'Camille',
    password: 'a-long-private-password',
  })
  expect(router.currentRoute.value.path).toBe('/')
  expect(page.get('header').text()).toContain('Camille')
  expect(page.text()).toContain('Votre espace est encore vide')
  expect(page.text()).not.toContain('Administration')
})

it('shows a generic unusable-link refusal without claiming account creation', async () => {
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account') return new Response(null, { status: 401 })
    if (path === '/api/auth/web/register')
      return Response.json({ error: 'invalid_token' }, { status: 400 })
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page } = await open('/accept-invitation#token=' + 'C'.repeat(64))
  await page.get('input[name="username"]').setValue('Camille')
  await page.get('input[name="password"]').setValue('a-long-private-password')
  await page.get('form').trigger('submit')
  await flushPromises()
  expect(page.get('[role="alert"]').text()).toContain('invalide, expiré, révoqué ou déjà utilisé')
  expect(page.text()).not.toContain('Votre compte est créé')
  expect(page.find('form').exists()).toBe(false)
})

it('reads a replacement invitation opened on the same page after a missing link', async () => {
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account') return new Response(null, { status: 401 })
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page, router } = await open('/accept-invitation')
  expect(page.find('form').exists()).toBe(false)
  await router.push('/accept-invitation#token=' + 'E'.repeat(64))
  await flushPromises()
  expect(page.find('input[name="username"]').exists()).toBe(true)
  expect(router.currentRoute.value.hash).toBe('')
})

it('keeps the acceptance page open until registration and automatic login finish', async () => {
  let finish: ((response: Response) => void) | undefined
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account') return Response.json({ ...alice, isAdmin: true })
    if (path === '/api/auth/web/register')
      return new Promise<Response>((resolve) => {
        finish = resolve
      })
    if (path === '/api/auth/web/login') return Response.json({ ...alice, id: 'new-account' })
    if (path === '/api/spaces/personal/elements') return Response.json([])
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page, router } = await open('/accept-invitation#token=' + 'F'.repeat(64))
  await page.get('input[name="username"]').setValue('Camille')
  await page.get('input[name="password"]').setValue('a-long-private-password')
  await page.get('form').trigger('submit')
  await flushPromises()
  await router.push('/connexion')
  expect(router.currentRoute.value.name).toBe('accept-invitation')
  finish!(Response.json({ ...alice, id: 'new-account' }, { status: 201 }))
  await flushPromises()
  expect(router.currentRoute.value.path).toBe('/')
  expect(page.text()).toContain('Votre espace est encore vide')
})

it('does not automatically sign in after another tab changes the session during registration', async () => {
  let logins = 0
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account') return new Response(null, { status: 401 })
    if (path === '/api/auth/web/register') {
      localStorage.setItem(sessionVersionKey, 'logged-out-in-another-tab')
      return Response.json(alice, { status: 201 })
    }
    if (path === '/api/auth/web/login') {
      logins++
      return Response.json(alice)
    }
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page } = await open('/accept-invitation#token=' + 'F'.repeat(64))
  await page.get('input[name="username"]').setValue('Alice')
  await page.get('input[name="password"]').setValue('a-long-private-password')
  await page.get('form').trigger('submit')
  await flushPromises()
  expect(logins).toBe(0)
  expect(page.text()).toContain('Votre compte est créé')
  expect(page.find('form').exists()).toBe(false)
})

it('preserves administrator input and an invitation created during same-session verification', async () => {
  const link = 'https://shelf.example.test/accept-invitation#token=' + 'F'.repeat(64)
  let finish: ((response: Response) => void) | undefined
  let unavailable = false
  vi.stubGlobal('fetch', async (path: string, options?: RequestInit) => {
    if (path === '/api/account')
      return unavailable
        ? new Response(null, { status: 503 })
        : Response.json({ ...alice, isAdmin: true })
    if (path === '/api/admin/invitations' && options?.method === 'POST')
      return new Promise<Response>((resolve) => {
        finish = resolve
      })
    if (path === '/api/admin/invitations') return Response.json([])
    if (path === '/api/auth/logout') return new Response(null, { status: 204 })
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page } = await open('/administration/invitations')
  await page.get('input[name="email"]').setValue('invitee@example.test')
  unavailable = true
  window.dispatchEvent(new Event('focus'))
  await flushPromises()
  expect(page.text()).toContain('Impossible de vérifier votre session')
  expect(page.get('input[name="email"]').isVisible()).toBe(false)
  unavailable = false
  await page
    .findAll('button')
    .find((b) => b.text() === 'Réessayer')!
    .trigger('click')
  await flushPromises()
  expect((page.get('input[name="email"]').element as HTMLInputElement).value).toBe(
    'invitee@example.test',
  )
  await page.get('form').trigger('submit')
  await flushPromises()
  window.dispatchEvent(new Event('focus'))
  await flushPromises()
  finish!(
    Response.json(
      {
        id: 'new-invitation',
        email: 'invitee@example.test',
        expiresAt: '2099-11-01T10:00:00Z',
        link,
      },
      { status: 201 },
    ),
  )
  await flushPromises()
  expect((page.get('textarea').element as HTMLTextAreaElement).value).toBe(link)
  await page
    .findAll('button')
    .find((b) => b.text() === 'Se déconnecter')!
    .trigger('click')
  await flushPromises()
  expect(page.find('textarea').exists()).toBe(false)
  expect(page.find('input[name="email"]').exists()).toBe(false)
})

it('does not render administration or call its API for an ordinary account even at a direct URL', async () => {
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account') return Response.json(alice)
    if (path === '/api/spaces/personal/elements') return Response.json([])
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page, router } = await open('/administration/invitations')
  expect(router.currentRoute.value.path).toBe('/')
  expect(page.text()).not.toContain('Administration')
})

it('shows a new invitation’s secret only in its initial display, copies it and revokes through the list', async () => {
  const link = 'https://shelf.example.test/accept-invitation#token=' + 'D'.repeat(64)
  const invitation = {
    id: 'invitation',
    email: 'camille@example.test',
    createdAt: '2026-10-25T10:00:00Z',
    expiresAt: '2099-11-01T10:00:00Z',
    revoked: false,
    consumed: false,
  }
  let exists = false
  let copied = ''
  vi.stubGlobal('navigator', {
    clipboard: {
      writeText: async (value: string) => {
        copied = value
      },
    },
  })
  vi.stubGlobal('fetch', async (path: string, options?: RequestInit) => {
    if (path === '/api/account') return Response.json({ ...alice, isAdmin: true })
    if (path === '/api/admin/invitations' && options?.method === 'POST') {
      exists = true
      return Response.json(
        { id: invitation.id, email: invitation.email, expiresAt: invitation.expiresAt, link },
        { status: 201 },
      )
    }
    if (path === '/api/admin/invitations') return Response.json(exists ? [invitation] : [])
    if (path === '/api/admin/invitations/invitation' && options?.method === 'DELETE') {
      invitation.revoked = true
      return new Response(null, { status: 204 })
    }
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page } = await open('/administration/invitations')
  expect(page.text()).toContain('Aucune invitation')
  await page.get('input[name="email"]').setValue(invitation.email)
  await page.get('form').trigger('submit')
  await flushPromises()
  expect(page.text()).toContain('il ne sera plus récupérable')
  await page
    .findAll('button')
    .find((b) => b.text() === 'Copier le lien')!
    .trigger('click')
  await flushPromises()
  expect(copied).toBe(link)
  await page
    .findAll('button')
    .find((b) => b.text() === 'Masquer le lien')!
    .trigger('click')
  expect(page.find('textarea').exists()).toBe(false)
  expect(page.text()).toContain(invitation.email)
  expect(page.text()).not.toContain(link)
  await page
    .findAll('button')
    .find((b) => b.text().startsWith('Révoquer'))!
    .trigger('click')
  await flushPromises()
  expect(page.text()).toContain('Révoquée')
  expect(page.findAll('button').some((b) => b.text().startsWith('Révoquer'))).toBe(false)
})
