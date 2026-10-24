<script setup lang="ts">
import { onMounted, onUnmounted, shallowRef } from 'vue'
import { useSession } from '../stores/session'

interface Element {
  id: string
  kind: 'file' | 'folder'
  name: string
  size: number | null
  addedAt: string
}

const session = useSession()
const elements = shallowRef<Element[]>([])
const loading = shallowRef(true)
const error = shallowRef(false)
let controller: AbortController | undefined
const date = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium' })
const size = new Intl.NumberFormat('fr-FR', {
  style: 'unit',
  unit: 'byte',
  notation: 'compact',
  maximumFractionDigits: 1,
})

async function load() {
  controller?.abort()
  const current = new AbortController()
  controller = current
  elements.value = []
  loading.value = true
  error.value = false
  try {
    const result = await session.request<Element[]>('/spaces/personal/elements', {
      signal: current.signal,
    })
    if (!current.signal.aborted) elements.value = result
  } catch {
    if (!current.signal.aborted) error.value = true
  } finally {
    if (!current.signal.aborted) loading.value = false
  }
}

onMounted(load)
onUnmounted(() => controller?.abort())
</script>

<template>
  <div class="min-h-dvh bg-muted/20">
    <header class="border-b border-default bg-default">
      <div
        class="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-4 px-5 py-5 sm:px-8"
      >
        <span class="text-2xl font-semibold tracking-tight text-primary">Shelf</span>
        <div class="flex min-w-0 items-center gap-3 sm:gap-6">
          <span class="max-w-32 truncate text-sm text-muted sm:max-w-64">{{
            session.account?.username
          }}</span>
          <UButton color="neutral" variant="outline" size="sm" @click="session.logout"
            >Se déconnecter</UButton
          >
        </div>
      </div>
    </header>
    <main class="mx-auto max-w-5xl px-5 py-10 sm:px-8 sm:py-14">
      <div class="mb-8 flex items-start justify-between gap-4">
        <div>
          <p class="mb-2 text-xs font-medium uppercase tracking-widest text-primary">Mon espace</p>
          <h1 class="text-2xl font-semibold tracking-tight sm:text-3xl">Espace personnel</h1>
          <p class="mt-2 text-sm text-muted">
            Vos fichiers et dossiers, à la racine de votre espace.
          </p>
        </div>
        <UButton
          color="neutral"
          variant="ghost"
          icon="i-lucide-refresh-cw"
          label="Actualiser l’espace personnel"
          :ui="{ label: 'sr-only' }"
          @click="session.verify"
        />
      </div>
      <section
        aria-label="Contenu de l’espace personnel"
        :aria-busy="loading"
        class="overflow-hidden rounded-xl border border-default bg-default"
      >
        <div
          v-if="loading"
          role="status"
          class="flex min-h-64 items-center justify-center gap-3 p-8 text-sm text-muted"
        >
          <UIcon name="i-lucide-loader-circle" class="size-5 animate-spin" />
          Chargement de votre espace…
        </div>
        <div
          v-else-if="error"
          class="flex min-h-64 flex-col items-center justify-center gap-5 p-8 text-center"
        >
          <p role="alert" class="text-sm text-error">
            Impossible de charger votre espace personnel.
          </p>
          <UButton color="neutral" variant="outline" @click="load">Réessayer</UButton>
        </div>
        <div
          v-else-if="elements.length === 0"
          role="status"
          class="flex min-h-72 flex-col items-center justify-center p-8 text-center"
        >
          <div class="mb-5 rounded-2xl bg-primary/10 p-4 text-primary">
            <UIcon name="i-lucide-folder" class="size-7" />
          </div>
          <h2 class="font-medium">Votre espace est encore vide</h2>
          <p class="mt-2 max-w-xs text-sm leading-relaxed text-muted">
            Vos fichiers et dossiers apparaîtront ici.
          </p>
        </div>
        <table v-else class="w-full table-fixed text-left text-sm">
          <caption class="sr-only">
            Fichiers et dossiers à la racine de l’espace personnel
          </caption>
          <thead class="border-b border-default bg-muted/40 text-xs text-muted">
            <tr>
              <th scope="col" class="px-5 py-3 font-medium">Nom</th>
              <th scope="col" class="w-28 px-5 py-3 text-right font-medium">Taille</th>
              <th scope="col" class="hidden w-44 px-5 py-3 font-medium sm:table-cell">Ajouté le</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-default">
            <tr v-for="element in elements" :key="element.id">
              <td class="px-5 py-4">
                <div class="flex items-center gap-3">
                  <UIcon
                    :name="element.kind === 'folder' ? 'i-lucide-folder' : 'i-lucide-file'"
                    class="size-5 shrink-0 text-primary"
                  /><span class="break-words">{{ element.name }}</span
                  ><span class="sr-only"
                    >({{ element.kind === 'folder' ? 'dossier' : 'fichier' }})</span
                  >
                </div>
              </td>
              <td class="px-5 py-4 text-right text-muted">
                {{ element.size === null ? '—' : size.format(element.size) }}
              </td>
              <td class="hidden px-5 py-4 text-muted sm:table-cell">
                {{ date.format(new Date(element.addedAt)) }}
              </td>
            </tr>
          </tbody>
        </table>
      </section>
      <p class="mt-5 text-xs text-muted">
        Cet espace vous appartient. Même les administrateurs n’ont pas accès à vos fichiers privés.
      </p>
    </main>
  </div>
</template>
