import { defineStore } from 'pinia'
import { computed, ref, shallowRef, watch } from 'vue'
import { ApiError } from '../api'
import { uploadFile, type UploadTicket } from '../transfers/upload'
import { useSession } from './session'

export interface UploadDestination {
  parentId: string | null
  label: string
}

export type ConflictChoice = 'keepBoth' | 'replace' | 'skip'

export interface Transfer {
  id: string
  batchId: string
  name: string
  size: number
  destination: UploadDestination
  status:
    | 'queued'
    | 'declaring'
    | 'uploading'
    | 'finalizing'
    | 'conflict'
    | 'completed'
    | 'skipped'
    | 'error'
  loaded: number
  error: string | null
}

export const useTransfers = defineStore('transfers', () => {
  const session = useSession()
  const items = ref<Transfer[]>([])
  const publication = shallowRef<{ parentId: string | null; revision: number } | null>(null)
  // Files stay in memory after an error, never in serialized state or browser storage.
  const work = new Map<string, { file: File; controller: AbortController }>()
  const policies = new Map<string, ConflictChoice>()
  const active = computed(() =>
    items.value.some((item) =>
      ['queued', 'declaring', 'uploading', 'finalizing', 'conflict'].includes(item.status),
    ),
  )

  function clear() {
    for (const { controller } of work.values()) controller.abort()
    work.clear()
    policies.clear()
    items.value = []
    publication.value = null
  }

  watch(() => session.privacyEpoch, clear, { flush: 'sync' })

  async function startBatch(files: File[], destination: UploadDestination) {
    if (session.status !== 'authenticated') return
    const batchId = crypto.randomUUID()
    const batch = files.map((file) => {
      const id = crypto.randomUUID()
      work.set(id, { file, controller: new AbortController() })
      items.value.push({
        id,
        batchId,
        name: file.name,
        size: file.size,
        destination: { ...destination },
        status: 'queued',
        loaded: 0,
        error: null,
      })
      return items.value[items.value.length - 1]!
    })
    // One file at a time per batch; different batches remain independent.
    for (const item of batch) await run(item)
  }

  function start(file: File, destination: UploadDestination) {
    return startBatch([file], destination)
  }

  async function applyChoice(item: Transfer, choice: ConflictChoice): Promise<void> {
    if (choice === 'skip') {
      item.status = 'skipped'
      item.error = null
      work.delete(item.id)
    } else await run(item, choice)
  }

  function resolveConflict(id: string, choice: ConflictChoice, following: boolean) {
    if (session.status !== 'authenticated') return
    const item = items.value.find((item) => item.id === id && item.status === 'conflict')
    if (!item) return
    if (following) policies.set(item.batchId, choice)
    for (const conflict of items.value.filter(
      (candidate) =>
        candidate.status === 'conflict' &&
        (candidate.id === id || (following && candidate.batchId === item.batchId)),
    )) {
      void applyChoice(conflict, choice)
    }
  }

  async function run(item: Transfer, policy?: Exclude<ConflictChoice, 'skip'>): Promise<void> {
    const pending = work.get(item.id)
    if (!pending) return
    const {
      file,
      controller: { signal },
    } = pending
    item.status = 'declaring'
    item.error = null
    item.loaded = 0
    try {
      const ticket = await session.request<UploadTicket>('/uploads', {
        method: 'POST',
        signal,
        body: JSON.stringify({
          name: file.name,
          size: file.size,
          contentType: file.type || 'application/octet-stream',
          parentId: item.destination.parentId,
          onConflict: policy,
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
      const result = await session.request<{ name: string }>(`/uploads/${ticket.id}/complete`, {
        method: 'POST',
        signal,
      })
      signal.throwIfAborted()
      item.name = result.name
      item.status = 'completed'
      work.delete(item.id)
      publication.value = {
        parentId: item.destination.parentId,
        revision: (publication.value?.revision ?? 0) + 1,
      }
    } catch (error) {
      if (signal.aborted) return
      if (
        error instanceof ApiError &&
        error.status === 409 &&
        error.code === 'name_conflict' &&
        !policy
      ) {
        // A failed confirmation did not publish this file. The policy belongs to
        // a new declaration; never replay the already confirmed batch entries.
        item.status = 'conflict'
        const following = policies.get(item.batchId)
        if (following) await applyChoice(item, following)
        return
      }
      const finalizing = item.status === 'finalizing'
      item.status = 'error'
      item.error =
        error instanceof ApiError
          ? error.code === 'name_conflict'
            ? 'Le serveur refuse ce conflit : un dossier de même nom ne peut pas être remplacé. Aucun remplacement n’a eu lieu.'
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

  return { items, active, publication, start, startBatch, resolveConflict, clear }
})
