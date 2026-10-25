<script setup lang="ts">
import { fr } from '@nuxt/ui/locale'
import { onMounted, onUnmounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { sessionVersionKey } from './api'
import { sessionRedirect } from './router'
import { useSession } from './stores/session'

const session = useSession()
const router = useRouter()
const route = useRoute()

watch(
  () => session.status,
  (status) => {
    const redirect = sessionRedirect(router, route, status, session.account?.isAdmin)
    if (redirect) void router.replace(redirect)
  },
)

function refresh() {
  if (document.visibilityState === 'visible' && session.status === 'authenticated')
    void session.verify()
}

function sessionChanged(event: StorageEvent) {
  if (event.storageArea === localStorage && (event.key === sessionVersionKey || event.key === null))
    void session.invalidate()
}

onMounted(() => {
  window.addEventListener('storage', sessionChanged)
  window.addEventListener('focus', refresh)
  window.addEventListener('pageshow', refresh)
  document.addEventListener('visibilitychange', refresh)
})
onUnmounted(() => {
  window.removeEventListener('storage', sessionChanged)
  window.removeEventListener('focus', refresh)
  window.removeEventListener('pageshow', refresh)
  document.removeEventListener('visibilitychange', refresh)
})
</script>

<template>
  <UApp :locale="fr">
    <main
      v-if="session.status === 'checking' && route.name !== 'accept-invitation'"
      class="flex min-h-dvh items-center justify-center p-6"
    >
      <p role="status" class="text-sm text-muted">Vérification de votre session…</p>
    </main>
    <main
      v-else-if="session.status === 'error' && route.name !== 'accept-invitation'"
      class="flex min-h-dvh flex-col items-center justify-center gap-5 p-6 text-center"
    >
      <p role="alert">
        {{
          session.logoutFailed
            ? 'La déconnexion n’a pas pu être confirmée. Vos données ont été masquées.'
            : 'Impossible de vérifier votre session. Vos données restent masquées.'
        }}
      </p>
      <UButton @click="session.logoutFailed ? session.logout() : session.verify()"
        >Réessayer</UButton
      >
    </main>
    <div v-show="!route.meta.admin || session.status === 'authenticated'">
      <RouterView
        v-if="
          route.meta.admin
            ? session.account?.isAdmin
            : (route.name === 'accept-invitation' ||
                !['checking', 'error'].includes(session.status)) &&
              (!route.meta.protected || session.status === 'authenticated')
        "
        :key="route.meta.protected ? session.account?.id : undefined"
      />
    </div>
  </UApp>
</template>
