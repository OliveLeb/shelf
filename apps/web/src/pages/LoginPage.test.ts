import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, expect, it, vi } from 'vite-plus/test'
import { createMemoryHistory, createRouter } from 'vue-router'
import LoginPage from './LoginPage.vue'

afterEach(() => vi.unstubAllGlobals())

it('keeps the identifier but clears the password after a rejected login', async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn().mockResolvedValue(new Response('{"error":"invalid_credentials"}', { status: 401 })),
  )
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/connexion', component: LoginPage }],
  })
  await router.push('/connexion')
  const page = mount(LoginPage, { global: { plugins: [createPinia(), router] } })
  await page.get('input[name="identifier"]').setValue('alice@example.test')
  await page.get('input[name="password"]').setValue('wrong-password')
  await page.get('form').trigger('submit')
  await flushPromises()
  expect(page.get('[role="alert"]').text()).toContain('Identifiant ou mot de passe incorrect')
  expect((page.get('input[name="identifier"]').element as HTMLInputElement).value).toBe(
    'alice@example.test',
  )
  expect((page.get('input[name="password"]').element as HTMLInputElement).value).toBe('')
  page.unmount()
})
