import { ApiError } from './api'

export interface Capabilities {
  canDownload: boolean
  canAdd: boolean
  canRename: boolean
  canMove: boolean
  canTrash: boolean
  canShare: boolean
  canCopy: boolean
}

export interface StorageElement {
  id: string
  kind: 'file' | 'folder'
  name: string
  parentId: string | null
  size: number | null
  contentType: string | null
  addedAt: string
  addedBy: { id: string; username: string } | null
}

export interface ElementEntry {
  element: StorageElement
  capabilities: Capabilities
}

export interface ElementContext {
  space: { kind: 'personal' | 'common' | 'shared' }
  element: StorageElement | null
  ancestors: { id: string; name: string }[]
  capabilities: Capabilities
}

export interface FolderContent {
  context: ElementContext
  entries: ElementEntry[]
}

export function mutationFailure(cause: unknown) {
  // A missing/5xx response cannot establish whether the write committed. Never offer a replay.
  const unknown = !(cause instanceof ApiError) || cause.status === 0 || cause.status >= 500
  const messages: Record<string, string> = {
    invalid_name:
      'Ce nom est invalide. Utilisez 1 à 255 caractères, sans / ni \\ ni caractère de contrôle, et un nom différent de « . » et « .. ».',
    name_conflict:
      'Un élément de ce nom existe déjà dans ce dossier. Aucun élément n’a été remplacé ni renommé automatiquement.',
    folder_cycle: 'Un dossier ne peut pas être déplacé vers lui-même ou un de ses descendants.',
  }
  const message = unknown
    ? 'Résultat inconnu : la réponse du serveur n’a pas été confirmée. Vérifiez la source et la destination avant toute nouvelle action. Aucun rejeu automatique.'
    : (messages[cause.code ?? ''] ??
      (cause.status === 403
        ? 'Accès refusé : cette opération n’est pas autorisée.'
        : cause.status === 404
          ? 'L’élément ou le dossier est introuvable ou n’est plus accessible.'
          : 'Le serveur a refusé cette opération. Aucun succès n’est confirmé.'))
  return { unknown, message }
}

export const date = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium' })
export const size = new Intl.NumberFormat('fr-FR', {
  style: 'unit',
  unit: 'byte',
  notation: 'compact',
  maximumFractionDigits: 1,
})
