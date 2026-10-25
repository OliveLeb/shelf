<script setup lang="ts">
import { shallowRef } from 'vue'
import { useSession } from '../stores/session'
import { useTransfers } from '../stores/transfers'

const session = useSession()
const transfers = useTransfers()
const confirmLogout = shallowRef(false)

function logout() {
  if (transfers.active) confirmLogout.value = true
  else void session.logout()
}

function leave() {
  confirmLogout.value = false
  void session.logout()
}
</script>

<template>
  <header class="border-b border-default bg-default">
    <div
      class="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-4 px-5 py-5 sm:px-8"
    >
      <RouterLink to="/" class="text-2xl font-semibold tracking-tight text-primary"
        >Shelf</RouterLink
      >
      <div class="flex min-w-0 items-center gap-3 sm:gap-6">
        <span class="max-w-32 truncate text-sm text-muted sm:max-w-64">{{
          session.account?.username
        }}</span>
        <UButton color="neutral" variant="outline" size="sm" @click="logout"
          >Se déconnecter</UButton
        >
      </div>
    </div>
    <UModal
      v-model:open="confirmLogout"
      title="Quitter pendant un envoi ?"
      description="La déconnexion interrompt les requêtes en cours et efface le suivi privé. Une publication déjà confirmée reste acquise ; une confirmation en cours peut avoir abouti."
    >
      <template #footer>
        <div class="flex w-full flex-wrap justify-end gap-3">
          <UButton color="neutral" variant="outline" @click="confirmLogout = false"
            >Continuer les envois</UButton
          >
          <UButton color="error" @click="leave">Interrompre et se déconnecter</UButton>
        </div>
      </template>
    </UModal>
  </header>
</template>
