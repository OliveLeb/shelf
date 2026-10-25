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
  status: 'declaring' | 'uploading' | 'finalizing' | 'completed' | 'error' | 'stopped' | 'unknown'
  loaded: number
  error: string | null
  canRetry: boolean
}

export const useTransfers = defineStore('transfers', () => {
  const session = useSession()
  const items = ref<Transfer[]>([])
  const publication = shallowRef<{ parentId: string | null; revision: number } | null>(null)
  // Files stay in memory after an error, never in serialized state or browser storage.
  const work = new Map<
    string,
    { file: File; controller: AbortController; uploadId?: string; uncertain?: boolean }
  >()
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
    const captured = { ...destination }
    items.value.push({
      id,
      name: file.name,
      size: file.size,
      destination: captured,
      status: 'declaring',
      loaded: 0,
      error: null,
      canRetry: false,
    })
    const item = items.value[items.value.length - 1]!
    work.set(id, { file, controller })
    await run(item)
  }

  async function run(item: Transfer) {
    const pending = work.get(item.id)!
    const { file, controller } = pending
    const { signal } = controller
    item.status = pending.uploadId ? 'uploading' : 'declaring'
    item.canRetry = false
    item.error = null
    item.loaded = 0
    try {
      const resume = () => session.request<UploadTicket>(`/uploads/${pending.uploadId}`, { signal })
      const ticket = pending.uploadId
        ? await resume()
        : await session.request<UploadTicket>('/uploads', {
            method: 'POST',
            signal,
            body: JSON.stringify({
              name: file.name,
              size: file.size,
              contentType: file.type || 'application/octet-stream',
              parentId: item.destination.parentId,
            }),
          })
      signal.throwIfAborted()
      pending.uploadId = ticket.id
      pending.uncertain = false
      item.status = 'uploading'
      await uploadFile(file, ticket, {
        signal,
        resume,
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
      work.delete(item.id)
      publication.value = {
        parentId: item.destination.parentId,
        revision: (publication.value?.revision ?? 0) + 1,
      }
    } catch (error) {
      if (signal.aborted) return
      const finalizing = item.status === 'finalizing'
      const refused = error instanceof ApiError && (error.status === 403 || error.status === 404)
      const uncertain =
        pending.uncertain ||
        (finalizing && error instanceof ApiError && (error.status === 0 || error.status >= 500))
      pending.uncertain = uncertain
      item.status = uncertain ? 'unknown' : 'error'
      item.canRetry = !refused
      item.error = uncertain
        ? 'Publication non confirmée : la réponse serveur manque. Réessayer vérifiera la reprise ; si elle n’est plus disponible, vérifiez la destination. Aucun nouvel envoi automatique.'
        : error instanceof ApiError
          ? error.code === 'name_conflict'
            ? 'Un élément porte déjà ce nom dans la destination. Aucun fichier n’a été remplacé.'
            : error.status === 403 || error.status === 404
              ? 'La destination n’est plus accessible en écriture.'
              : finalizing
                ? 'La confirmation a échoué ou sa réponse a été perdue. Publication non confirmée ; vérifiez la destination avant un nouvel envoi.'
                : 'Impossible de préparer ou reprendre l’envoi. Vérifiez votre connexion puis réessayez.'
          : error instanceof Error
            ? error.message
            : 'L’envoi a échoué.'
      if (refused) work.delete(item.id)
    }
  }

  function stop(id: string) {
    const item = items.value.find((entry) => entry.id === id)
    const pending = work.get(id)
    if (!item || !pending || !['declaring', 'uploading', 'finalizing'].includes(item.status)) return
    const finalizing = item.status === 'finalizing' || pending.uncertain
    pending.uncertain = finalizing
    pending.controller.abort()
    item.status = finalizing ? 'unknown' : 'stopped'
    item.canRetry = true
    item.error = finalizing
      ? 'Finalisation interrompue côté navigateur. Le fichier peut déjà être publié ; aucune annulation serveur n’est confirmée.'
      : null
  }

  async function retry(id: string) {
    const item = items.value.find((entry) => entry.id === id)
    const pending = work.get(id)
    if (session.status !== 'authenticated' || !item?.canRetry || !pending) return
    pending.controller = new AbortController()
    await run(item)
  }

  return { items, active, publication, start, clear, stop, retry }
})
