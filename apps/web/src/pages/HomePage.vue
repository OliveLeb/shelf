<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '../api'
import { useSession } from '../stores/session'
import { useTransfers } from '../stores/transfers'

interface Element {
  id: string
  kind: 'file' | 'folder'
  name: string
  size: number | null
  addedAt: string
}

const session = useSession()
const transfers = useTransfers()
const route = useRoute()
const router = useRouter()
const loading = shallowRef(true)
const error = shallowRef(false)
const parentId = computed(() =>
  typeof route.params.folderId === 'string' ? route.params.folderId : null,
)
const folderName = computed(() =>
  !loading.value && !error.value && typeof route.query.nom === 'string'
    ? route.query.nom
    : 'Dossier',
)
const destinationLabel = computed(() =>
  parentId.value ? `Espace personnel / ${folderName.value}` : 'Espace personnel',
)
const elements = shallowRef<Element[]>([])
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
    const result = await session.request<Element[]>(
      parentId.value ? `/folders/${parentId.value}/elements` : '/spaces/personal/elements',
      {
        signal: current.signal,
      },
    )
    if (!current.signal.aborted) elements.value = result
  } catch (cause) {
    if (!current.signal.aborted) {
      error.value = true
      // A previous account's folder URL is not an authorized destination for
      // the next account. Do not display its old label or keep that URL.
      if (
        parentId.value &&
        cause instanceof ApiError &&
        (cause.status === 403 || cause.status === 404)
      )
        await router.replace('/')
    }
  } finally {
    if (!current.signal.aborted) loading.value = false
  }
}

function selectFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file) void transfers.start(file, { parentId: parentId.value, label: destinationLabel.value })
  input.value = ''
}

async function download(id: string) {
  try {
    const link = await session.request<{ url: string }>(`/files/${id}/download`)
    window.location.assign(link.url)
  } catch {
    downloadError.value = true
  }
}
const downloadError = shallowRef(false)

watch(
  () => transfers.publication,
  (event) => {
    if (event && event.parentId === parentId.value) void load()
  },
)
onMounted(load)
onUnmounted(() => controller?.abort())
</script>

<template>
  <main class="mx-auto w-full max-w-5xl px-5 py-10 sm:px-8 sm:py-14">
    <RouterLink v-if="parentId" to="/" class="mb-5 inline-block text-sm text-primary underline"
      >Retour à l’espace personnel</RouterLink
    >
    <div class="mb-8 flex flex-wrap items-start justify-between gap-4">
      <div>
        <p class="mb-2 text-xs font-medium uppercase tracking-widest text-primary">Mon espace</p>
        <h1 class="break-words text-2xl font-semibold tracking-tight sm:text-3xl">
          {{ parentId ? folderName : 'Espace personnel' }}
        </h1>
        <p class="mt-2 text-sm text-muted">Destination de l’envoi : {{ destinationLabel }}</p>
      </div>
      <div class="flex items-center gap-2">
        <label
          class="relative inline-flex cursor-pointer items-center gap-2 rounded-md bg-primary px-3 py-2 text-sm font-medium text-inverted hover:bg-primary/90 focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary has-disabled:cursor-not-allowed has-disabled:opacity-50"
        >
          <UIcon name="i-lucide-upload" class="size-4" />
          Envoyer un fichier
          <input
            type="file"
            aria-label="Envoyer un fichier"
            class="absolute inset-0 w-full cursor-pointer opacity-0"
            :disabled="loading || error"
            @change="selectFile"
          />
        </label>
        <UButton
          color="neutral"
          variant="ghost"
          icon="i-lucide-refresh-cw"
          label="Actualiser l’espace personnel"
          :ui="{ label: 'sr-only' }"
          @click="session.verify"
        />
      </div>
    </div>
    <p v-if="downloadError" role="alert" class="mb-4 text-sm text-error">
      Impossible de préparer le téléchargement.
    </p>
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
        <p role="alert" class="text-sm text-error">Impossible de charger votre espace personnel.</p>
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
          Fichiers et dossiers de la destination
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
                />
                <RouterLink
                  v-if="element.kind === 'folder'"
                  :to="{
                    name: 'folder',
                    params: { folderId: element.id },
                    query: { nom: element.name },
                  }"
                  class="break-words text-primary underline-offset-4 hover:underline"
                  >{{ element.name }}</RouterLink
                >
                <button
                  v-else
                  class="break-words text-left text-primary underline-offset-4 hover:underline"
                  @click="download(element.id)"
                >
                  {{ element.name }}<span class="sr-only"> — Télécharger</span>
                </button>
                <span class="sr-only"
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
</template>
