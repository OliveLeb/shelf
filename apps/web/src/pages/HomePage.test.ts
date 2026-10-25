import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, expect, it, vi } from 'vite-plus/test'
import { createMemoryHistory, createRouter } from 'vue-router'
import HomePage from './HomePage.vue'

afterEach(() => vi.unstubAllGlobals())

it('retries a failed root listing and displays the real elements', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 503 }))
      .mockResolvedValueOnce(
        Response.json([
          {
            id: 'folder-1',
            kind: 'folder',
            name: 'Photos de famille',
            size: null,
            addedAt: '2026-10-24T12:00:00Z',
          },
        ]),
      ),
  )
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: HomePage },
      { path: '/dossiers/:folderId', name: 'folder', component: HomePage },
    ],
  })
  await router.push('/')
  const page = mount(HomePage, { global: { plugins: [createPinia(), router] } })
  await flushPromises()
  expect(page.get('[role="alert"]').text()).toContain('Impossible de charger')
  await page
    .findAll('button')
    .find((button) => button.text() === 'Réessayer')!
    .trigger('click')
  await flushPromises()
  expect(page.text()).toContain('Photos de famille')
  expect(page.text()).not.toContain('Impossible de charger')
  page.unmount()
})
