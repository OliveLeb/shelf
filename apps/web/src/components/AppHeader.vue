<script setup lang="ts">
import { useSession } from '../stores/session'

const session = useSession()
</script>

<template>
  <header class="border-b border-default bg-default">
    <div
      class="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-4 px-5 py-5 sm:px-8"
    >
      <RouterLink
        to="/"
        class="rounded text-2xl font-semibold tracking-tight text-primary focus-visible:outline-2"
        >Shelf</RouterLink
      >
      <div class="flex min-w-0 items-center gap-3 sm:gap-6">
        <span class="max-w-32 truncate text-sm text-muted sm:max-w-64">{{
          session.account?.username
        }}</span>
        <UButton color="neutral" variant="outline" size="sm" @click="session.logout"
          >Se déconnecter</UButton
        >
      </div>
      <nav
        v-if="session.account?.isAdmin"
        aria-label="Espaces et administration"
        class="flex w-full flex-wrap gap-2 border-t border-default pt-4"
      >
        <UButton to="/" color="neutral" variant="ghost" active-class="bg-elevated" exact
          >Espace personnel</UButton
        >
        <UButton
          to="/administration/invitations"
          color="neutral"
          variant="ghost"
          active-class="bg-elevated"
          icon="i-lucide-shield"
          >Administration</UButton
        >
      </nav>
    </div>
  </header>
</template>
