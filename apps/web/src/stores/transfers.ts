import { defineStore } from 'pinia'
import { computed, ref, shallowRef, watch } from 'vue'
import { ApiError } from '../api'
import { uploadFile, type UploadTicket } from '../transfers/upload'
import { useSession } from './session'

export interface UploadDestination {
  parentId: string | null
  label: string
}

export interface Transfer {
  id: string
  name: string
  size: number
  destination: UploadDestination
  status: 'declaring' | 'uploading' | 'finalizing' | 'completed' | 'error'
  loaded: number
  error: string | null
}

export const useTransfers = defineStore('transfers', () => {
  const session = useSession()
  const items = ref<Transfer[]>([])
  const publication = shallowRef<{ parentId: string | null; revision: number } | null>(null)
  // Files stay in memory after an error, never in serialized state or browser storage.
  const work = new Map<string, { file: File; controller: AbortController }>()
  const active = computed(() =>
    items.value.some((item) => ['declaring', 'uploading', 'finalizing'].includes(item.status)),
  )

  function clear() {
    for (const { controller } of work.values()) controller.abort()
    work.clear()
    items.value = []
    publication.value = null
  }

  watch(() => session.privacyEpoch, clear, { flush: 'sync' })

  async function start(file: File, destination: UploadDestination) {
    if (session.status !== 'authenticated') return
    const id = crypto.randomUUID()
    const controller = new AbortController()
    const { signal } = controller
    const captured = { ...destination }
    items.value.push({
      id,
      name: file.name,
      size: file.size,
      destination: captured,
      status: 'declaring',
      loaded: 0,
      error: null,
    })
    const item = items.value[items.value.length - 1]!
    work.set(id, { file, controller })
    try {
      const ticket = await session.request<UploadTicket>('/uploads', {
        method: 'POST',
        signal,
        body: JSON.stringify({
          name: file.name,
          size: file.size,
          contentType: file.type || 'application/octet-stream',
          parentId: captured.parentId,
        }),
      })
      signal.throwIfAborted()
      item.status = 'uploading'
      await uploadFile(file, ticket, {
        signal,
        onProgress: (loaded) => {
          if (!signal.aborted) item.loaded = loaded
        },
      })
      signal.throwIfAborted()
      item.loaded = file.size
      item.status = 'finalizing'
      await session.request(`/uploads/${ticket.id}/complete`, { method: 'POST', signal })
      signal.throwIfAborted()
      item.status = 'completed'
      work.delete(id)
      publication.value = {
        parentId: captured.parentId,
        revision: (publication.value?.revision ?? 0) + 1,
      }
    } catch (error) {
      if (signal.aborted) return
      const finalizing = item.status === 'finalizing'
      item.status = 'error'
      item.error =
        error instanceof ApiError
          ? error.code === 'name_conflict'
            ? 'Un élément porte déjà ce nom dans la destination. Aucun fichier n’a été remplacé.'
            : error.status === 403 || error.status === 404
              ? 'La destination n’est plus accessible en écriture.'
              : finalizing
                ? 'La confirmation a échoué ou sa réponse a été perdue. Publication non confirmée ; vérifiez la destination avant un nouvel envoi.'
                : 'Impossible de déclarer l’envoi. Vérifiez votre connexion puis choisissez à nouveau le fichier.'
          : error instanceof Error
            ? error.message
            : 'L’envoi a échoué.'
    }
  }

  return { items, active, publication, start, clear }
})
