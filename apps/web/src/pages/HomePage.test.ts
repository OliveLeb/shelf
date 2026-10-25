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
