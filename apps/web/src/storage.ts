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

export const date = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium' })
export const size = new Intl.NumberFormat('fr-FR', {
  style: 'unit',
  unit: 'byte',
  notation: 'compact',
  maximumFractionDigits: 1,
})
