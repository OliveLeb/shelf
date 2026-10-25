import type { Pinia } from 'pinia'
import {
  createRouter,
  createWebHistory,
  type RouteLocationNormalized,
  type Router,
  type RouterHistory,
} from 'vue-router'
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
  // Folder access is checked by the server on every list/declaration request.
  return route.name === 'personal' || route.name === 'folder' ? route.fullPath : '/'
}

export function sessionRedirect(
  router: Router,
  route: RouteLocationNormalized,
  status: ReturnType<typeof useSession>['status'],
) {
  if (route.meta.protected && status === 'anonymous')
    return { name: 'login', query: { retour: route.fullPath } }
  if (route.name === 'login' && status === 'authenticated')
    return destination(router, route.query.retour)
}

export function makeRouter(pinia: Pinia, history: RouterHistory = createWebHistory()) {
  const router = createRouter({
    history,
    routes: [
      { path: '/', name: 'personal', component: HomePage, meta: { protected: true } },
      {
        path: '/dossiers/:folderId([0-9a-fA-F-]{36})',
        name: 'folder',
        component: HomePage,
        meta: { protected: true },
      },
      { path: '/connexion', name: 'login', component: LoginPage },
      { path: '/:pathMatch(.*)*', redirect: '/' },
    ],
  })
  router.beforeEach(async (to) => {
    const session = useSession(pinia)
    if (session.status === 'checking') await session.verify()
    return sessionRedirect(router, to, session.status)
  })
  return router
}
