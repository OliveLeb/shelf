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
import AcceptInvitationPage from './pages/AcceptInvitationPage.vue'
import InvitationsPage from './pages/InvitationsPage.vue'
import { useSession } from './stores/session'

export function destination(router: Router, value: unknown, isAdmin = false) {
  if (
    typeof value !== 'string' ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    value.includes('\\')
  )
    return '/'
  const route = router.resolve(value)
  return route.name === 'personal' || (route.name === 'invitations' && isAdmin)
    ? route.fullPath
    : '/'
}

export function sessionRedirect(
  router: Router,
  route: RouteLocationNormalized,
  status: ReturnType<typeof useSession>['status'],
  isAdmin = false,
) {
  if (route.meta.protected && status === 'anonymous')
    return { name: 'login', query: { retour: route.fullPath } }
  if (route.meta.admin && status === 'authenticated' && !isAdmin) return '/'
  if (route.name === 'login' && status === 'authenticated')
    return destination(router, route.query.retour, isAdmin)
}

export function makeRouter(pinia: Pinia, history: RouterHistory = createWebHistory()) {
  const router = createRouter({
    history,
    routes: [
      { path: '/', name: 'personal', component: HomePage, meta: { protected: true } },
      { path: '/connexion', name: 'login', component: LoginPage },
      { path: '/accept-invitation', name: 'accept-invitation', component: AcceptInvitationPage },
      {
        path: '/administration/invitations',
        name: 'invitations',
        component: InvitationsPage,
        meta: { protected: true, admin: true },
      },
      { path: '/:pathMatch(.*)*', redirect: '/' },
    ],
  })
  router.beforeEach(async (to) => {
    const session = useSession(pinia)
    if (session.status === 'checking') await session.verify()
    return sessionRedirect(router, to, session.status, session.account?.isAdmin)
  })
  return router
}
