import type { Pinia } from 'pinia'
import { createRouter, createWebHistory, type Router, type RouterHistory } from 'vue-router'
import HomePage from './pages/HomePage.vue'
import LoginPage from './pages/LoginPage.vue'
import { useSession } from './stores/session'

export function destination(router: Router, value: unknown) {
  if (
    typeof value !== 'string' ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    value.includes('\\')
  )
    return '/'
  const route = router.resolve(value)
  // Only the personal root is delivered here. Unknown or unauthorized routes cannot be resumed.
  return route.name === 'personal' ? route.fullPath : '/'
}

export function makeRouter(pinia: Pinia, history: RouterHistory = createWebHistory()) {
  const router = createRouter({
    history,
    routes: [
      { path: '/', name: 'personal', component: HomePage, meta: { protected: true } },
      { path: '/connexion', name: 'login', component: LoginPage },
      { path: '/:pathMatch(.*)*', redirect: '/' },
    ],
  })
  router.beforeEach(async (to) => {
    const session = useSession(pinia)
    if (session.status === 'checking') await session.verify()
    if (to.meta.protected && session.status === 'anonymous') {
      return { name: 'login', query: { retour: to.fullPath } }
    }
    if (to.name === 'login' && session.status === 'authenticated')
      return destination(router, to.query.retour)
  })
  return router
}
