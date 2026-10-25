<script setup lang="ts">
import { fr } from '@nuxt/ui/locale'
import { onMounted, onUnmounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { sessionVersionKey } from './api'
import SessionHeader from './components/SessionHeader.vue'
import TransferPanel from './components/TransferPanel.vue'
import { sessionRedirect } from './router'
import { useSession } from './stores/session'
import { useTransfers } from './stores/transfers'

const session = useSession()
const transfers = useTransfers()
const router = useRouter()
const route = useRoute()

watch(
  () => session.status,
  (status) => {
    const redirect = sessionRedirect(router, route, status)
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

function beforeUnload(event: BeforeUnloadEvent) {
  if (transfers.active) event.preventDefault()
}

onMounted(() => {
  window.addEventListener('storage', sessionChanged)
  window.addEventListener('focus', refresh)
  window.addEventListener('pageshow', refresh)
  window.addEventListener('beforeunload', beforeUnload)
  document.addEventListener('visibilitychange', refresh)
})
onUnmounted(() => {
  window.removeEventListener('storage', sessionChanged)
  window.removeEventListener('focus', refresh)
  window.removeEventListener('pageshow', refresh)
  window.removeEventListener('beforeunload', beforeUnload)
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
    <div v-else-if="session.status === 'authenticated'" class="flex min-h-dvh flex-col bg-muted/20">
      <SessionHeader />
      <RouterView :key="`${session.account?.id}:${route.path}`" class="flex-1" />
      <TransferPanel />
    </div>
    <RouterView v-else-if="!route.meta.protected" />
  </UApp>
</template>
