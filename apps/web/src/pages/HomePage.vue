<script setup lang="ts">
import { computed, onUnmounted, shallowRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '../api'
import ElementList from '../components/ElementList.vue'
import ElementNameDialog from '../components/ElementNameDialog.vue'
import FileDetails from '../components/FileDetails.vue'
import MoveElementsDialog from '../components/MoveElementsDialog.vue'
import type { ElementEntry, FolderContent, StorageElement } from '../storage'
import { useSession } from '../stores/session'

const session = useSession()
const route = useRoute()
const router = useRouter()
const content = shallowRef<FolderContent | null>(null)
const loading = shallowRef(true)
const error = shallowRef('')
const naming = shallowRef<{ element?: StorageElement } | null>(null)
const moving = shallowRef<{ entries: ElementEntry[]; targetId: string | null } | null>(null)
const folderId = computed(() =>
  typeof route.params.folderId === 'string' ? route.params.folderId : '',
)
const sort = computed(() =>
  ['name', 'addedAt', 'size'].includes(String(route.query.tri)) ? String(route.query.tri) : 'name',
)
const order = computed(() => (route.query.ordre === 'desc' ? 'desc' : 'asc'))
const filter = computed({
  get: () => (typeof route.query.filtre === 'string' ? route.query.filtre : ''),
  set: (value) => {
    void router.replace({ query: { ...route.query, filtre: value || undefined } })
  },
})
const fileId = computed(() => (typeof route.query.fichier === 'string' ? route.query.fichier : ''))
const title = computed(
  () => content.value?.context.element?.name ?? (folderId.value ? 'Dossier' : 'Espace personnel'),
)
const breadcrumbs = computed(() => [
  { label: 'Espace personnel', to: '/' },
  ...(content.value?.context.ancestors.map((ancestor) => ({
    label: ancestor.name,
    to: `/dossiers/${ancestor.id}`,
  })) ?? []),
  ...(content.value?.context.element ? [{ label: content.value.context.element.name }] : []),
])
const requestPath = computed(() => {
  const query = new URLSearchParams({ sort: sort.value, order: order.value })
  if (folderId.value) query.set('folderId', folderId.value)
  return `/spaces/personal/browse?${query}`
})
let controller: AbortController | undefined

async function load() {
  controller?.abort()
  const current = new AbortController()
  controller = current
  content.value = null
  loading.value = true
  error.value = ''
  try {
    const result = await session.request<FolderContent>(requestPath.value, {
      signal: current.signal,
    })
    if (!current.signal.aborted) content.value = result
  } catch (cause) {
    if (!current.signal.aborted)
      error.value =
        cause instanceof ApiError && (cause.status === 404 || cause.status === 403)
          ? 'Ce dossier est introuvable ou vous n’y avez plus accès.'
          : 'Impossible de charger ce dossier. Vérifiez votre connexion puis réessayez.'
  } finally {
    if (!current.signal.aborted) loading.value = false
  }
}
function inspect(id: string) {
  void router.push({ query: { ...route.query, fichier: id } })
}
function closeDetails() {
  void router.replace({ query: { ...route.query, fichier: undefined } })
}
function changeSort(value: string) {
  void router.push({ query: { ...route.query, tri: value } })
}
function openMove(entries: ElementEntry[], targetId?: string) {
  moving.value = { entries, targetId: targetId ?? (folderId.value || null) }
}

watch(folderId, () => {
  naming.value = null
  moving.value = null
})
watch(requestPath, load, { immediate: true, flush: 'sync' })
onUnmounted(() => controller?.abort())
</script>

<template>
  <div class="min-h-dvh bg-muted/20">
    <header class="border-b border-default bg-default">
      <div
        class="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-4 px-5 py-5 sm:px-8"
      >
        <RouterLink
          to="/"
          class="text-2xl font-semibold tracking-tight text-primary focus-visible:outline-2 focus-visible:outline-primary"
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
      </div>
    </header>
    <main class="mx-auto max-w-5xl px-5 py-8 sm:px-8 sm:py-12">
      <nav v-if="content" aria-label="Fil d’Ariane" class="mb-6">
        <UBreadcrumb
          as="div"
          :items="breadcrumbs"
          :ui="{ list: 'flex-wrap', linkLabel: 'whitespace-normal break-words' }"
        />
      </nav>
      <div class="mb-7 flex items-start justify-between gap-4">
        <div class="min-w-0">
          <p class="mb-2 text-xs font-medium uppercase tracking-widest text-primary">Mon espace</p>
          <h1 class="break-words text-2xl font-semibold tracking-tight sm:text-3xl">{{ title }}</h1>
          <p class="mt-2 text-sm text-muted">
            {{
              folderId
                ? 'Contenu de ce dossier uniquement.'
                : 'Vos fichiers et dossiers, à la racine de votre espace.'
            }}
          </p>
        </div>
        <UButton
          color="neutral"
          variant="outline"
          icon="i-lucide-refresh-cw"
          :disabled="loading"
          @click="session.status === 'authenticated' ? session.verify() : load()"
          >Actualiser</UButton
        >
      </div>
      <div class="mb-5 flex flex-wrap items-end gap-3">
        <UButton
          v-if="content?.context.capabilities.canAdd"
          icon="i-lucide-folder-plus"
          @click="naming = {}"
          >Nouveau dossier</UButton
        >
        <UFormField label="Filtrer ce dossier" class="min-w-0 flex-1 basis-64">
          <UInput
            id="folder-filter"
            v-model="filter"
            placeholder="Un nom dans ce dossier…"
            icon="i-lucide-search"
            class="w-full"
          />
        </UFormField>
        <label class="block text-sm font-medium text-default">
          Trier par
          <select
            id="folder-sort"
            :value="sort"
            class="mt-1 block w-36 rounded-md border-0 bg-default px-2.5 py-1.5 text-base/5 text-highlighted ring ring-inset ring-accented focus-visible:outline-2 focus-visible:outline-primary md:text-sm"
            @change="changeSort(($event.target as HTMLSelectElement).value)"
          >
            <option value="name">Nom</option>
            <option value="addedAt">Date d’ajout</option>
            <option value="size">Taille</option>
          </select>
        </label>
        <UButton
          color="neutral"
          variant="outline"
          :icon="order === 'asc' ? 'i-lucide-arrow-up' : 'i-lucide-arrow-down'"
          @click="
            router.push({ query: { ...route.query, ordre: order === 'asc' ? 'desc' : 'asc' } })
          "
          >{{ order === 'asc' ? 'Croissant' : 'Décroissant'
          }}<span class="sr-only"> — inverser l’ordre</span></UButton
        >
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
          <UIcon name="i-lucide-loader-circle" class="size-5 animate-spin" />Chargement du dossier…
        </div>
        <div
          v-else-if="error"
          class="flex min-h-64 flex-col items-center justify-center gap-5 p-8 text-center"
        >
          <p role="alert" class="text-sm text-error">{{ error }}</p>
          <UButton color="neutral" variant="outline" @click="load">Réessayer</UButton>
          <RouterLink v-if="folderId" to="/" class="text-sm text-primary underline"
            >Retour à l’espace personnel</RouterLink
          >
        </div>
        <ElementList
          v-else-if="content"
          :key="folderId"
          :entries="content.entries"
          :filter="filter"
          :at-root="!folderId"
          @inspect="inspect"
          @rename="naming = { element: $event.element }"
          @move="openMove"
          @clear-filter="filter = ''"
        />
      </section>
      <p class="mt-5 text-xs leading-relaxed text-muted">
        Déplacez une sélection avec « Déplacer vers… » ou glissez-la sur un dossier de la liste. Le
        dépôt de fichiers locaux et le réordonnancement ne sont pas proposés ici.
      </p>
      <p class="mt-3 text-xs leading-relaxed text-muted">
        Cet espace vous appartient. Même les administrateurs n’ont pas accès à vos fichiers privés.
      </p>
    </main>
    <ElementNameDialog
      v-if="naming"
      :parent-id="folderId || null"
      :element="naming.element"
      @close="naming = null"
      @changed="load"
    />
    <MoveElementsDialog
      v-if="moving"
      :entries="moving.entries"
      :source-id="folderId || null"
      :target-id="moving.targetId"
      @close="moving = null"
      @changed="load"
    />
    <FileDetails
      v-if="fileId && !error"
      :key="fileId"
      :file-id="fileId"
      @close="closeDetails"
      @unavailable="load"
    />
  </div>
</template>
