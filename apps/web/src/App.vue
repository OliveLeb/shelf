<script setup lang="ts">
import { fr } from '@nuxt/ui/locale'
import { onMounted, onUnmounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { destination } from './router'
import { useSession } from './stores/session'

const session = useSession()
const router = useRouter()
const route = useRoute()

watch(
  () => session.status,
  (status) => {
    if (status === 'anonymous' && route.meta.protected) {
      void router.replace({ name: 'login', query: { retour: route.fullPath } })
    } else if (status === 'authenticated' && route.name === 'login') {
      void router.replace(destination(router, route.query.retour))
    }
  },
)

function refresh() {
  if (document.visibilityState === 'visible' && session.status === 'authenticated')
    void session.verify()
}

onMounted(() => {
  window.addEventListener('focus', refresh)
  window.addEventListener('pageshow', refresh)
  document.addEventListener('visibilitychange', refresh)
})
onUnmounted(() => {
  window.removeEventListener('focus', refresh)
  window.removeEventListener('pageshow', refresh)
  document.removeEventListener('visibilitychange', refresh)
})
</script>

<template>
  <UApp :locale="fr">
    <main
      v-if="session.status === 'checking'"
      class="flex min-h-dvh items-center justify-center p-6"
    >
      <p role="status" class="text-sm text-muted">Vérification de votre session…</p>
    </main>
    <main
      v-else-if="session.status === 'error'"
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
    <RouterView v-else :key="session.account?.id" />
  </UApp>
</template>
