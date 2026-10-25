import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, expect, it, vi } from 'vite-plus/test'
import { createMemoryHistory, createRouter } from 'vue-router'
import HomePage from './HomePage.vue'

enableAutoUnmount(afterEach)
afterEach(() => vi.unstubAllGlobals())

const capabilities = {
  canDownload: false,
  canAdd: true,
  canRename: true,
  canMove: true,
  canTrash: true,
  canShare: true,
  canCopy: true,
}
const folder = {
  id: 'folder-1',
  kind: 'folder',
  name: 'Photos de famille',
  size: null,
  addedAt: '2026-10-24T12:00:00Z',
}
const file = {
  id: 'file-1',
  kind: 'file',
  name: 'Lettre.txt',
  size: 37,
  addedAt: '2026-10-23T12:00:00Z',
}
function browse(elements = [folder, file], current: typeof folder | null = null) {
  return {
    context: { space: { kind: 'personal' }, element: current, ancestors: [], capabilities },
    entries: elements.map((element) => ({
      element,
      capabilities: { ...capabilities, canDownload: element.kind === 'file' },
    })),
  }
}
async function open(path = '/') {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'personal', component: HomePage },
      { path: '/dossiers/:folderId', name: 'folder', component: HomePage },
    ],
  })
  await router.push(path)
  const page = mount(HomePage, { global: { plugins: [createPinia(), router] } })
  await flushPromises()
  return { page, router }
}

it('retries a failed root listing and displays the real elements', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 503 }))
      .mockResolvedValueOnce(Response.json(browse())),
  )
  const { page } = await open()
  expect(page.get('[role="alert"]').text()).toContain('Impossible de charger')
  await page
    .findAll('button')
    .find((button) => button.text() === 'Réessayer')!
    .trigger('click')
  await flushPromises()
  expect(page.text()).toContain('Photos de famille')
  expect(page.text()).not.toContain('Impossible de charger')
})

it('filters only the current listing, clears hidden selections and never carries them into another folder', async () => {
  vi.stubGlobal('fetch', async (path: string) =>
    Response.json(path.includes('folderId=') ? browse([], folder) : browse()),
  )
  const { page, router } = await open()
  await page.get('input[aria-label="Sélectionner Lettre.txt"]').setValue(true)
  expect(page.text()).toContain('1 élément sélectionné')
  expect(page.findAll('button').some((button) => button.text() === 'Informations')).toBe(true)
  await page.get('input[aria-label="Sélectionner Photos de famille"]').setValue(true)
  expect(page.text()).toContain('2 éléments sélectionnés')
  expect(page.findAll('button').some((button) => button.text() === 'Informations')).toBe(false)
  await page.get('input#folder-filter').setValue('PHOTOS')
  await flushPromises()
  expect(page.text()).toContain(folder.name)
  expect(page.text()).not.toContain(file.name)
  expect(page.text()).not.toContain('1 élément sélectionné')
  expect(router.currentRoute.value.query.filtre).toBe('PHOTOS')
  await page.get('input[aria-label="Sélectionner Photos de famille"]').setValue(true)
  await page.get(`a[href="/dossiers/${folder.id}"]`).trigger('click')
  await flushPromises()
  expect(page.text()).toContain('Ce dossier est vide')
  expect(page.text()).not.toContain('1 élément sélectionné')
})

it('distinguishes no matches from empty content and removes stale names when direct access is lost', async () => {
  let unavailable = false
  vi.stubGlobal('fetch', async () =>
    unavailable ? new Response(null, { status: 404 }) : Response.json(browse([file], folder)),
  )
  const { page, router } = await open('/dossiers/folder-1?tri=size&ordre=desc&filtre=introuvable')
  expect(page.text()).toContain('Aucun résultat dans ce dossier')
  expect(page.get('h1').text()).toBe(folder.name)
  await router.replace({ query: { tri: 'size', ordre: 'desc' } })
  await flushPromises()
  expect(page.text()).toContain(file.name)
  unavailable = true
  await page
    .findAll('button')
    .find((button) => button.text() === 'Actualiser')!
    .trigger('click')
  await flushPromises()
  expect(page.get('[role="alert"]').text()).toContain(
    'Ce dossier est introuvable ou vous n’y avez plus accès',
  )
  expect(page.text()).not.toContain(file.name)
  expect(page.text()).not.toContain(folder.name)
})

it('uses the authorized file context and removes stale information and selection when downloading is refused', async () => {
  let fileGone = false
  vi.stubGlobal('fetch', async (path: string) => {
    if (path.endsWith('/download')) {
      fileGone = true
      return new Response(null, { status: 404 })
    }
    if (path.endsWith('/context'))
      return Response.json({
        ...browse().context,
        element: { ...file, contentType: 'text/plain', addedBy: { username: 'Alice' } },
        capabilities: { ...capabilities, canDownload: true },
      })
    return Response.json(browse(fileGone ? [folder] : [folder, file]))
  })
  const { page } = await open()
  await page.get('input[aria-label="Sélectionner Lettre.txt"]').setValue(true)
  await page
    .findAll('button')
    .find((button) => button.text() === 'Informations')!
    .trigger('click')
  await flushPromises()
  const dialog = document.querySelector('[role="dialog"]')!
  expect(dialog.textContent).toContain('37 octets')
  expect(dialog.textContent).toContain('Alice')
  const download = Array.from(dialog.querySelectorAll('button')).find((button) =>
    button.textContent?.includes('Télécharger'),
  )!
  download.click()
  await flushPromises()
  expect(dialog.querySelector('[role="alert"]')?.textContent).toContain(
    'Ce fichier est introuvable ou vous n’y avez plus accès',
  )
  expect(dialog.textContent).not.toContain('37 octets')
  expect(dialog.textContent).not.toContain('Télécharger')
  expect(page.text()).not.toContain(file.name)
  expect(page.text()).not.toContain('1 élément sélectionné')
})

it('revalidates the whole folder after a child disappears and hides its other stale entries', async () => {
  let folderGone = false
  const sibling = { ...file, id: 'file-2', name: 'Autre fichier.txt' }
  vi.stubGlobal('fetch', async () =>
    folderGone
      ? new Response(null, { status: 404 })
      : Response.json(browse([file, sibling], folder)),
  )
  const { page } = await open('/dossiers/folder-1')
  expect(page.get('h1').text()).toBe(folder.name)
  expect(page.text()).toContain(sibling.name)
  folderGone = true
  await page
    .findAll('button')
    .find((button) => button.text().startsWith(file.name))!
    .trigger('click')
  await flushPromises()
  expect(page.get('[role="alert"]').text()).toContain(
    'Ce dossier est introuvable ou vous n’y avez plus accès',
  )
  expect(page.text()).not.toContain(folder.name)
  expect(page.text()).not.toContain(file.name)
  expect(page.text()).not.toContain(sibling.name)
})

function button(text: string, root: ParentNode = document) {
  const found = Array.from(root.querySelectorAll('button')).find(
    (element) => element.textContent?.trim() === text,
  )
  expect(found, `button ${text}`).toBeDefined()
  return found!
}

it('creates in the current folder only after confirmation and keeps rename conflicts visible', async () => {
  let created = false
  let creation: unknown
  let confirm: (response: Response) => void = () => {}
  vi.stubGlobal('fetch', async (path: string, options: RequestInit) => {
    if (path === '/api/folders') {
      creation = JSON.parse(String(options.body))
      const response = await new Promise<Response>((resolve) => {
        confirm = resolve
      })
      created = true
      return response
    }
    if (path.endsWith('/rename')) return Response.json({ error: 'name_conflict' }, { status: 409 })
    return Response.json(
      browse(created ? [{ ...folder, id: 'created', name: 'Voyages' }, file] : [file], folder),
    )
  })
  const { page } = await open('/dossiers/folder-1')
  page
    .findAll('button')
    .find((element) => element.text() === 'Nouveau dossier')!
    .element.click()
  await flushPromises()
  const dialog = document.querySelector('[role="dialog"]')!
  const input = dialog.querySelector('input')!
  input.value = 'Voyages'
  input.dispatchEvent(new Event('input', { bubbles: true }))
  button('Créer', dialog).click()
  await flushPromises()
  expect(creation).toEqual({ name: 'Voyages', parentId: folder.id, space: 'personal' })
  expect(page.text()).not.toContain('Voyages')
  confirm(Response.json({ ...folder, id: 'created', name: 'Voyages' }, { status: 201 }))
  await flushPromises()
  expect(page.text()).toContain('Voyages')
  await page.get('input[aria-label="Sélectionner Lettre.txt"]').setValue(true)
  page
    .findAll('button')
    .find((element) => element.text() === 'Renommer')!
    .element.click()
  await flushPromises()
  const rename = document.querySelector('[role="dialog"]')!
  const name = rename.querySelector('input')!
  name.value = 'Voyages'
  name.dispatchEvent(new Event('input', { bubbles: true }))
  button('Enregistrer', rename).click()
  await flushPromises()
  expect(rename.querySelector('[role="alert"]')?.textContent).toContain('existe déjà')
  expect(name.value).toBe('Voyages')
  expect(page.text()).toContain('Lettre.txt')
})

it('reports partial moves per element and retries only confirmed failures, never successes or unknown writes', async () => {
  const second = { ...file, id: 'file-2', name: 'Journal.txt' }
  const third = { ...file, id: 'file-3', name: 'Brouillon.txt' }
  const moved: string[] = []
  const destinations: unknown[] = []
  let retry = false
  vi.stubGlobal('fetch', async (path: string, options: RequestInit) => {
    if (path.endsWith('/move')) {
      destinations.push(JSON.parse(String(options.body)))
      moved.push(path)
      if (path.includes('file-3')) throw new TypeError('connection lost')
      if (path.includes('file-2') && !retry)
        return Response.json({ error: 'name_conflict' }, { status: 409 })
      return Response.json(path.includes('file-2') ? second : file)
    }
    return Response.json(
      path.includes('folderId=') ? browse([], folder) : browse([folder, file, second, third]),
    )
  })
  const { page } = await open()
  for (const name of ['Lettre.txt', 'Journal.txt', 'Brouillon.txt'])
    await page.get(`input[aria-label="Sélectionner ${name}"]`).setValue(true)
  page
    .findAll('button')
    .find((element) => element.text() === 'Déplacer vers…')!
    .element.click()
  await flushPromises()
  let dialog = document.querySelector('[role="dialog"]')!
  button('Photos de famille', dialog).click()
  await flushPromises()
  dialog = document.querySelector('[role="dialog"]')!
  button('Déplacer ici', dialog).click()
  await flushPromises()
  expect(dialog.textContent).toContain('1 déplacé')
  expect(dialog.textContent).toContain('1 refusé')
  expect(dialog.textContent).toContain('1 inconnu')
  expect(dialog.textContent).toContain('existe déjà')
  expect(dialog.textContent).toContain('Résultat inconnu')
  retry = true
  button('Réessayer les refus', dialog).click()
  await flushPromises()
  expect(dialog.textContent).toContain('2 déplacés')
  expect(dialog.textContent).toContain('1 inconnu')
  expect(moved).toEqual([
    '/api/elements/file-1/move',
    '/api/elements/file-2/move',
    '/api/elements/file-3/move',
    '/api/elements/file-2/move',
  ])
  expect(destinations).toEqual(
    Array.from({ length: 4 }, () => ({ parentId: folder.id, space: 'personal' })),
  )
})

it('accepts only an internal drag onto an allowed folder and asks for confirmation before writing', async () => {
  const writes: string[] = []
  vi.stubGlobal('fetch', async (path: string, options: RequestInit) => {
    if (options.method === 'POST') {
      writes.push(path)
      return Response.json(file)
    }
    return Response.json(path.includes('folderId=') ? browse([], folder) : browse())
  })
  const { page } = await open()
  const target = page.get(`a[href="/dossiers/${folder.id}"]`).element.closest('tr')!
  const source = page.get('input[aria-label="Sélectionner Lettre.txt"]').element.closest('tr')!
  const external = { types: ['Files'] }
  target.dispatchEvent(new Event('drop', { bubbles: true }))
  await flushPromises()
  expect(document.querySelector('[role="dialog"]')).toBeNull()
  const types: string[] = []
  const transfer = { types, setData: (type: string) => types.push(type) }
  source.dispatchEvent(
    Object.assign(new Event('dragstart', { bubbles: true }), { dataTransfer: transfer }),
  )
  target.dispatchEvent(
    Object.assign(new Event('drop', { bubbles: true }), { dataTransfer: external }),
  )
  await flushPromises()
  expect(document.querySelector('[role="dialog"]')).toBeNull()
  source.dispatchEvent(
    Object.assign(new Event('dragstart', { bubbles: true }), { dataTransfer: transfer }),
  )
  source.dispatchEvent(
    Object.assign(new Event('drop', { bubbles: true }), { dataTransfer: transfer }),
  )
  await flushPromises()
  expect(document.querySelector('[role="dialog"]')).toBeNull()
  source.dispatchEvent(
    Object.assign(new Event('dragstart', { bubbles: true }), { dataTransfer: transfer }),
  )
  target.dispatchEvent(
    Object.assign(new Event('drop', { bubbles: true }), { dataTransfer: transfer }),
  )
  await flushPromises()
  const dialog = document.querySelector('[role="dialog"]')!
  expect(dialog.textContent).toContain('Destination : Espace personnel / Photos de famille')
  expect(writes).toEqual([])
  button('Déplacer ici', dialog).click()
  await flushPromises()
  expect(dialog.textContent).toContain('1 déplacé')
  expect(writes).toEqual(['/api/elements/file-1/move'])
})
