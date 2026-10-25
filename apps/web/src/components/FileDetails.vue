<script setup lang="ts">
import { onMounted, onUnmounted, shallowRef } from 'vue'
import { ApiError } from '../api'
import { date, size, type ElementContext } from '../storage'
import { useSession } from '../stores/session'

const { fileId } = defineProps<{ fileId: string }>()
const emit = defineEmits<{ close: []; unavailable: [id: string] }>()
const session = useSession()
const context = shallowRef<ElementContext | null>(null)
const loading = shallowRef(true)
const downloading = shallowRef(false)
const error = shallowRef('')
const controller = new AbortController()

function fail(cause: unknown) {
  if (controller.signal.aborted) return
  if (cause instanceof ApiError && (cause.status === 404 || cause.status === 403)) {
    context.value = null
    error.value = 'Ce fichier est introuvable ou vous n’y avez plus accès.'
    emit('unavailable', fileId)
  } else
    error.value =
      'Impossible de récupérer les informations ou le lien de téléchargement. Réessayez.'
}
async function load() {
  loading.value = true
  context.value = null
  error.value = ''
  try {
    const result = await session.request<ElementContext>(
      `/elements/${encodeURIComponent(fileId)}/context`,
      { signal: controller.signal },
    )
    if (result.space.kind !== 'personal' || result.element?.kind !== 'file')
      throw new ApiError(404, null)
    if (!controller.signal.aborted) context.value = result
  } catch (cause) {
    fail(cause)
  } finally {
    if (!controller.signal.aborted) loading.value = false
  }
}
async function download() {
  if (!context.value?.capabilities.canDownload || downloading.value) return
  downloading.value = true
  error.value = ''
  try {
    // Ask for a fresh capability on every click; never retain a signed URL in page state.
    const result = await session.request<{ url: string }>(
      `/files/${encodeURIComponent(fileId)}/download`,
      { signal: controller.signal },
    )
    if (controller.signal.aborted) return
    const link = document.createElement('a')
    link.href = result.url
    link.rel = 'noreferrer'
    link.referrerPolicy = 'no-referrer'
    document.body.append(link)
    link.click()
    link.remove()
  } catch (cause) {
    fail(cause)
  } finally {
    if (!controller.signal.aborted) downloading.value = false
  }
}
onMounted(load)
onUnmounted(() => controller.abort())
</script>

<template>
  <UModal
    :open="true"
    title="Informations du fichier"
    description="Consultez les informations puis téléchargez le fichier original."
    :ui="{ title: 'break-words' }"
    @update:open="!$event && emit('close')"
  >
    <template #close>
      <button
        type="button"
        aria-label="Fermer les informations"
        class="absolute end-4 top-4 rounded-md p-2 text-muted hover:bg-elevated focus-visible:outline-2 focus-visible:outline-primary"
        @click="emit('close')"
      >
        <UIcon name="i-lucide-x" class="size-5" />
      </button>
    </template>
    <template #body>
      <p v-if="loading" role="status" class="text-sm text-muted">Chargement des informations…</p>
      <template v-else>
        <div v-if="error" class="mb-5 space-y-4">
          <p role="alert" class="text-sm text-error">{{ error }}</p>
          <UButton color="neutral" variant="outline" @click="load">Réessayer</UButton>
        </div>
        <template v-if="context?.element">
          <h2 class="mb-6 break-words text-lg font-semibold">{{ context.element.name }}</h2>
          <dl class="space-y-4 text-sm">
            <div>
              <dt class="text-muted">Emplacement</dt>
              <dd class="mt-1 break-words">
                Espace personnel<span v-for="ancestor in context.ancestors" :key="ancestor.id">
                  / {{ ancestor.name }}</span
                >
              </dd>
            </div>
            <div>
              <dt class="text-muted">Taille</dt>
              <dd class="mt-1">
                {{ size.format(context.element.size ?? 0) }} · {{ context.element.size }} octets
              </dd>
            </div>
            <div>
              <dt class="text-muted">Type</dt>
              <dd class="mt-1 break-words">{{ context.element.contentType }}</dd>
            </div>
            <div>
              <dt class="text-muted">Ajouté le</dt>
              <dd class="mt-1">{{ date.format(new Date(context.element.addedAt)) }}</dd>
            </div>
            <div>
              <dt class="text-muted">Ajouté par</dt>
              <dd class="mt-1 break-words">
                {{ context.element.addedBy?.username ?? 'Compte supprimé' }}
              </dd>
            </div>
          </dl>
          <UButton
            v-if="context.capabilities.canDownload"
            class="mt-7"
            icon="i-lucide-download"
            :loading="downloading"
            @click="download"
            >Télécharger</UButton
          >
          <p class="mt-3 text-xs text-muted">
            Le téléchargement démarre directement depuis le stockage privé.
          </p>
        </template>
      </template>
    </template>
  </UModal>
</template>
