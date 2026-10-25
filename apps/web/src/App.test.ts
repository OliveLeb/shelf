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

async function chooseFile(page: Awaited<ReturnType<typeof open>>['page'], name = 'preuve.txt') {
  const input = page.get('input[type="file"]')
  Object.defineProperty(input.element, 'files', {
    configurable: true,
    value: [new File(['bonjour'], name, { type: 'text/plain' })],
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

it('retries only missing multipart bytes and waits for publication before reporting completion', async () => {
  const received: string[] = []
  let fail = true
  let confirm: ((response: Response) => void) | undefined
  const ticket = (numbers: number[]) => ({
    id: 'multipart',
    method: 'PUT',
    headers: { 'X-Part-Header': 'prescribed' },
    partSize: 3,
    expiresAt: '2099-01-01T00:00:00Z',
    parts: numbers.map((number) => ({ number, url: `https://s3.example.test/${number}` })),
  })
  class MultipartS3 extends S3Request {
    number = ''
    override open = vi.fn<(method: string, url: string) => void>((_method, url) => {
      this.number = url.split('/').at(-1)!
    })
    override async send(body: Blob) {
      expect(this.withCredentials).toBe(false)
      expect(this.setRequestHeader).toHaveBeenCalledWith('X-Part-Header', 'prescribed')
      received.push(`${this.number}:${await body.text()}`)
      if (this.number === '2' && fail) this.dispatchEvent(new Event('error'))
      else this.dispatchEvent(new Event('load'))
    }
  }
  vi.stubGlobal('XMLHttpRequest', MultipartS3)
  let resumed = false
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/account') return Response.json(alice)
    if (path === '/api/spaces/personal/elements') return Response.json([])
    if (path === '/api/uploads') return Response.json(ticket([1, 2, 3]))
    if (path === '/api/uploads/multipart') {
      const numbers = resumed ? [] : [2, 3]
      resumed = true
      return Response.json(ticket(numbers))
    }
    if (path === '/api/uploads/multipart/complete')
      return new Promise<Response>((resolve) => {
        confirm = resolve
      })
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page } = await open()
  await chooseFile(page)
  expect(page.get('aside').text()).toContain('Erreur')
  expect(received).toEqual(['1:bon', '2:jou'])
  fail = false
  await page
    .get('aside')
    .findAll('button')
    .find((button) => button.text() === 'Réessayer')!
    .trigger('click')
  await flushPromises()
  expect(received).toEqual(['1:bon', '2:jou', '2:jou', '3:r'])
  expect(page.get('aside').text()).toContain('Finalisation')
  expect(page.get('aside').text()).not.toContain('Terminé')
  confirm!(Response.json({ id: 'published' }, { status: 201 }))
  await flushPromises()
  expect(page.get('aside').text()).toContain('Terminé')
})

it.each([
  {
    scenario: 'batches',
    expected: ['1:bon', '2:jou', '3:r'],
    status: 'Terminé',
    reads: 2,
    confirmed: true,
  },
  {
    scenario: 'expired',
    expected: ['1:bon', '2:jou', '3:r'],
    status: 'Terminé',
    reads: 1,
    confirmed: true,
  },
  {
    scenario: 'denied',
    expected: ['1:bon', '1:bon'],
    status: 'Erreur',
    reads: 1,
    confirmed: false,
  },
  { scenario: 'no-progress', expected: ['1:bon'], status: 'Erreur', reads: 1, confirmed: false },
])(
  'handles multipart %s without unbounded writes',
  async ({ scenario, expected, status, reads: expectedReads, confirmed: expectedConfirmation }) => {
    const received: string[] = []
    let reads = 0
    let confirmed = false
    const ticket = (numbers: number[], expired = false) => ({
      id: 'paged',
      method: 'PUT',
      headers: {},
      partSize: 3,
      expiresAt: expired ? '2000-01-01T00:00:00Z' : '2099-01-01T00:00:00Z',
      parts: numbers.map((number) => ({ number, url: `https://s3.example.test/${number}` })),
    })
    class PagedS3 extends S3Request {
      number = ''
      override open = vi.fn<(method: string, url: string) => void>((_method, url) => {
        this.number = url.split('/').at(-1)!
      })
      override async send(body: Blob) {
        received.push(`${this.number}:${await body.text()}`)
        this.status = scenario === 'denied' ? 403 : 200
        this.dispatchEvent(new Event('load'))
      }
    }
    vi.stubGlobal('XMLHttpRequest', PagedS3)
    vi.stubGlobal('fetch', async (path: string) => {
      if (path === '/api/account') return Response.json(alice)
      if (path === '/api/spaces/personal/elements') return Response.json([])
      if (path === '/api/uploads') return Response.json(ticket([1], scenario === 'expired'))
      if (path === '/api/uploads/paged') {
        reads++
        if (reads > 4) throw new Error('Unbounded resume loop')
        return Response.json(
          ticket(
            scenario === 'no-progress' || scenario === 'denied'
              ? [1]
              : scenario === 'expired'
                ? [1, 2, 3]
                : reads === 1
                  ? [2]
                  : [3],
          ),
        )
      }
      if (path === '/api/uploads/paged/complete') {
        confirmed = true
        return Response.json({ id: 'published' }, { status: 201 })
      }
      throw new Error(`Unexpected request: ${path}`)
    })
    const { page } = await open()
    await chooseFile(page)
    expect(page.get('aside').text()).toContain(status)
    expect(confirmed).toBe(expectedConfirmation)
    expect(received).toEqual(expected)
    expect(reads).toBe(expectedReads)
  },
)

it('stops one file without stopping its neighbor and never claims to undo interrupted publication', async () => {
  const requests: S3Request[] = []
  let declarations = 0
  let finishConfirmation: ((response: Response) => void) | undefined
  let completeSignal: AbortSignal | undefined
  class WaitingS3 extends S3Request {
    override send() {
      requests.push(this)
    }
  }
  vi.stubGlobal('XMLHttpRequest', WaitingS3)
  vi.stubGlobal('fetch', async (path: string, options?: RequestInit) => {
    if (path === '/api/account') return Response.json(alice)
    if (path === '/api/spaces/personal/elements') return Response.json([])
    if (path === '/api/uploads')
      return Response.json({
        id: `upload-${++declarations}`,
        method: 'PUT',
        headers: {},
        partSize: 7,
        parts: [{ number: 1, url: 'https://s3.example.test/signed' }],
      })
    if (path === '/api/uploads/upload-2/complete') {
      completeSignal = options!.signal!
      return new Promise<Response>((resolve) => {
        finishConfirmation = resolve
      })
    }
    if (path === '/api/uploads/upload-2')
      return Response.json({ error: 'upload_not_found' }, { status: 404 })
    throw new Error(`Unexpected request: ${path}`)
  })
  const { page } = await open()
  await chooseFile(page)
  await chooseFile(page, 'voisin.txt')
  const first = page.findAll('article')[0]!
  const second = page.findAll('article')[1]!
  const firstAbort = vi.spyOn(requests[0]!, 'abort')
  const secondAbort = vi.spyOn(requests[1]!, 'abort')
  await first.get('button').trigger('click')
  expect(first.text()).toContain('Arrêté')
  expect(firstAbort).toHaveBeenCalledOnce()
  expect(secondAbort).not.toHaveBeenCalled()
  requests[1]!.dispatchEvent(new Event('load'))
  await flushPromises()
  expect(second.text()).toContain('Finalisation')
  await second.get('button').trigger('click')
  expect(completeSignal!.aborted).toBe(true)
  expect(second.text()).toContain('résultat inconnu')
  finishConfirmation!(Response.json({ id: 'published' }, { status: 201 }))
  await flushPromises()
  expect(second.text()).not.toContain('Terminé')
  await second.get('button').trigger('click')
  await flushPromises()
  expect(second.text()).toContain('résultat inconnu')
  expect(second.find('button').exists()).toBe(false)
  expect(declarations).toBe(2)
})
