<script setup lang="ts">
import { shallowRef, useTemplateRef } from 'vue'

const { destinationLabel, disabled = false } = defineProps<{
  destinationLabel: string
  disabled?: boolean
}>()
const emit = defineEmits<{ files: [files: File[]] }>()
const picker = useTemplateRef<HTMLInputElement>('picker')
const dragging = shallowRef(false)
const rejectedFolders = shallowRef(false)

function select(event: Event) {
  const input = event.target as HTMLInputElement
  if (disabled) return
  rejectedFolders.value = false
  const files = Array.from(input.files ?? []).filter((file) => {
    if (!file.webkitRelativePath) return true
    rejectedFolders.value = true
    return false
  })
  if (files.length) emit('files', files)
  input.value = ''
}

function external(event: DragEvent) {
  return Array.from(event.dataTransfer?.types ?? []).includes('Files')
}

function over(event: DragEvent) {
  if (!external(event)) return
  event.preventDefault()
  if (disabled) return
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'
  dragging.value = true
}

function leave(event: DragEvent) {
  if (
    event.relatedTarget instanceof Node &&
    (event.currentTarget as HTMLElement).contains(event.relatedTarget)
  )
    return
  dragging.value = false
}

function drop(event: DragEvent) {
  if (!external(event)) return
  event.preventDefault()
  dragging.value = false
  if (disabled) return
  rejectedFolders.value = false
  const files: File[] = []
  // Read entries during the drop event; never traverse a local directory.
  for (const item of Array.from(event.dataTransfer?.items ?? [])) {
    if (item.kind !== 'file') continue
    const entry = item.webkitGetAsEntry?.()
    if (!entry || entry.isDirectory) {
      rejectedFolders.value = true
      continue
    }
    const file = item.getAsFile()
    if (file) files.push(file)
  }
  if (files.length) emit('files', files)
}
</script>

<template>
  <section
    aria-label="Envoi depuis votre ordinateur"
    class="mb-6 rounded-xl border-2 border-dashed p-5 transition-colors"
    :class="dragging ? 'border-primary bg-primary/10' : 'border-default bg-default'"
    @dragover="over"
    @dragleave="leave"
    @drop="drop"
  >
    <div class="flex flex-wrap items-center justify-between gap-4">
      <div class="min-w-0">
        <p class="text-sm font-medium">
          Envoyer vers : <span class="break-words">{{ destinationLabel }}</span>
        </p>
        <p class="mt-1 text-sm text-muted">
          Déposez des fichiers depuis votre ordinateur ou utilisez le sélecteur.
        </p>
      </div>
      <UButton icon="i-lucide-upload" :disabled="disabled" @click="picker?.click()"
        >Choisir des fichiers</UButton
      >
      <input
        ref="picker"
        type="file"
        multiple
        :disabled="disabled"
        class="sr-only"
        tabindex="-1"
        aria-label="Choisir des fichiers à envoyer"
        @change="select"
      />
    </div>
    <p class="mt-3 text-xs text-muted">
      Fichiers uniquement : l’import de dossiers locaux n’est pas disponible.
    </p>
    <p v-if="rejectedFolders" role="alert" class="mt-3 text-sm text-error">
      Les dossiers ne sont pas importés. Sélectionnez leurs fichiers avec le sélecteur ; les autres
      fichiers déposés sont envoyés.
    </p>
  </section>
</template>
