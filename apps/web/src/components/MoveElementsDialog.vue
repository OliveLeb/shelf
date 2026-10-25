<script setup lang="ts">
import { computed, onUnmounted, shallowRef, watch } from 'vue'
import {
  mutationFailure,
  type ElementEntry,
  type FolderContent,
  type StorageElement,
} from '../storage'
import { useSession } from '../stores/session'

const { entries, sourceId, targetId } = defineProps<{
  entries: ElementEntry[]
  sourceId: string | null
  targetId: string | null
}>()
const emit = defineEmits<{ close: []; changed: [] }>()
const session = useSession()
const destination = shallowRef(targetId)
const content = shallowRef<FolderContent | null>(null)
const loading = shallowRef(false)
const error = shallowRef('')
const busy = shallowRef(false)
type Result = {
  entry: ElementEntry
  state: 'ready' | 'moving' | 'moved' | 'refused' | 'unknown'
  message: string
}
const results = shallowRef<Result[]>([])
const folders = computed(
  () => content.value?.entries.filter(({ element }) => element.kind === 'folder') ?? [],
)
const blocked = computed(() => {
  if (!content.value) return ''
  const ids = [
    content.value.context.element?.id,
    ...content.value.context.ancestors.map((ancestor) => ancestor.id),
  ]
  if (entries.some(({ element }) => element.kind === 'folder' && ids.includes(element.id)))
    return 'Un dossier ne peut pas être déplacé vers lui-même ou un de ses descendants.'
  if (destination.value === sourceId) return 'Les éléments sont déjà dans ce dossier.'
  if (!content.value.context.capabilities.canAdd)
    return 'Vous ne pouvez pas ajouter d’élément dans ce dossier.'
  return ''
})
const moved = computed(() => results.value.filter((result) => result.state === 'moved').length)
const refused = computed(() => results.value.filter((result) => result.state === 'refused').length)
const unknown = computed(() => results.value.filter((result) => result.state === 'unknown').length)
const path = computed(() =>
  [
    'Espace personnel',
    ...(content.value?.context.ancestors.map((ancestor) => ancestor.name) ?? []),
    ...(content.value?.context.element ? [content.value.context.element.name] : []),
  ].join(' / '),
)
let active = true
let controller: AbortController | undefined

async function load() {
  controller?.abort()
  const current = new AbortController()
  controller = current
  content.value = null
  loading.value = true
  error.value = ''
  try {
    const query = destination.value ? `?folderId=${encodeURIComponent(destination.value)}` : ''
    const value = await session.request<FolderContent>(`/spaces/personal/browse${query}`, {
      signal: current.signal,
    })
    if (!current.signal.aborted) content.value = value
  } catch {
    if (!current.signal.aborted)
      error.value =
        'Impossible d’ouvrir cette destination. Elle est introuvable, inaccessible ou la connexion est interrompue.'
  } finally {
    if (!current.signal.aborted) loading.value = false
  }
}

async function move() {
  if (busy.value || !content.value || blocked.value || loading.value) return
  if (!results.value.length)
    results.value = entries.map((entry) => ({ entry, state: 'ready', message: '' }))
  const pending = results.value.filter(
    (result) => result.state === 'ready' || result.state === 'refused',
  )
  if (!pending.length) return
  busy.value = true
  const parentId = destination.value
  function update(id: string, state: Result['state'], message = '') {
    results.value = results.value.map((result) =>
      result.entry.element.id === id ? { ...result, state, message } : result,
    )
  }
  for (const { entry } of pending) {
    if (!active) break
    update(entry.element.id, 'moving')
    try {
      await session.request<StorageElement>(`/elements/${entry.element.id}/move`, {
        method: 'POST',
        body: JSON.stringify({ parentId, space: 'personal' }),
      })
      if (active) update(entry.element.id, 'moved', 'Déplacé')
    } catch (cause) {
      if (active) {
        const failure = mutationFailure(cause)
        update(entry.element.id, failure.unknown ? 'unknown' : 'refused', failure.message)
      }
    }
  }
  if (active) {
    busy.value = false
    // Reload both sides. No optimistic row removal and no storage cache in Pinia.
    emit('changed')
    await load()
  }
}

watch(destination, load, { immediate: true })
onUnmounted(() => {
  active = false
  controller?.abort()
})
</script>

<template>
  <UModal
    :open="true"
    title="Déplacer vers…"
    :description="`${entries.length} élément${entries.length > 1 ? 's' : ''} · Espace personnel uniquement. Aucun remplacement.`"
    :dismissible="!busy"
    :close="!busy"
    @update:open="!$event && !busy && emit('close')"
  >
    <template #body>
      <p v-if="loading" role="status" class="text-sm text-muted">Chargement de la destination…</p>
      <div v-else-if="error" class="space-y-4">
        <p role="alert" class="text-sm text-error">{{ error }}</p>
        <UButton color="neutral" variant="outline" @click="load">Réessayer la destination</UButton>
        <UButton
          v-if="destination && !results.length"
          color="neutral"
          variant="outline"
          @click="destination = null"
          >Retour à l’espace personnel</UButton
        >
      </div>
      <div v-else-if="content" class="space-y-4">
        <p class="break-words text-sm font-medium">Destination : {{ path }}</p>
        <template v-if="!results.length">
          <nav aria-label="Navigation de destination" class="flex flex-wrap gap-2">
            <UButton
              v-if="destination"
              color="neutral"
              variant="outline"
              size="sm"
              @click="destination = null"
              >Espace personnel</UButton
            >
            <UButton
              v-if="destination"
              color="neutral"
              variant="outline"
              size="sm"
              icon="i-lucide-arrow-up"
              @click="destination = content.context.element?.parentId ?? null"
              >Dossier parent</UButton
            >
          </nav>
          <div class="max-h-64 overflow-y-auto rounded-lg border border-default">
            <p v-if="!folders.length" class="p-4 text-sm text-muted">Aucun sous-dossier.</p>
            <button
              v-for="{ element } in folders"
              :key="element.id"
              type="button"
              class="flex min-h-11 w-full items-center gap-3 border-b border-default px-4 py-3 text-left text-sm last:border-0 hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary focus-visible:-outline-offset-2 disabled:opacity-50"
              :disabled="entries.some((entry) => entry.element.id === element.id)"
              @click="destination = element.id"
            >
              <UIcon name="i-lucide-folder" class="size-5 shrink-0 text-primary" />
              <span class="min-w-0 flex-1 break-words">{{ element.name }}</span>
              <UIcon name="i-lucide-chevron-right" class="size-4 shrink-0 text-muted" />
            </button>
          </div>
          <p v-if="blocked" role="status" class="text-sm text-muted">{{ blocked }}</p>
        </template>
      </div>
      <div v-if="results.length" class="mt-5 space-y-3" aria-live="polite">
        <p role="status" class="text-sm font-medium">
          {{ moved }} {{ moved > 1 ? 'déplacés' : 'déplacé' }} · {{ refused }}
          {{ refused > 1 ? 'refusés' : 'refusé' }} · {{ unknown }}
          {{ unknown > 1 ? 'inconnus' : 'inconnu' }} / {{ entries.length }}
        </p>
        <ul class="max-h-64 space-y-3 overflow-y-auto text-sm">
          <li
            v-for="result in results"
            :key="result.entry.element.id"
            class="rounded-lg border border-default p-3"
          >
            <p class="break-words font-medium">{{ result.entry.element.name }}</p>
            <p
              class="mt-1"
              :class="
                result.state === 'refused' || result.state === 'unknown'
                  ? 'text-error'
                  : 'text-muted'
              "
            >
              {{
                result.message ||
                (result.state === 'moving' ? 'Déplacement en cours…' : 'En attente')
              }}
            </p>
          </li>
        </ul>
      </div>
    </template>
    <template #footer>
      <div class="flex w-full flex-wrap justify-end gap-3">
        <UButton color="neutral" variant="outline" :disabled="busy" @click="emit('close')">{{
          results.length ? 'Fermer' : 'Annuler'
        }}</UButton>
        <UButton
          v-if="!results.length || refused"
          class="disabled:opacity-40"
          :loading="busy"
          :disabled="loading || !content || !!blocked"
          @click="move"
          >{{ results.length ? 'Réessayer les refus' : 'Déplacer ici' }}</UButton
        >
      </div>
    </template>
  </UModal>
</template>
