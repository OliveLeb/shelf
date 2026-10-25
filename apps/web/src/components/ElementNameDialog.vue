<script setup lang="ts">
import { onUnmounted, shallowRef } from 'vue'
import { mutationFailure, type StorageElement } from '../storage'
import { useSession } from '../stores/session'

const { parentId, element } = defineProps<{ parentId: string | null; element?: StorageElement }>()
const emit = defineEmits<{ close: []; changed: [] }>()
const session = useSession()
const name = shallowRef(element?.name ?? '')
const busy = shallowRef(false)
const error = shallowRef('')
const unknown = shallowRef(false)
let active = true
onUnmounted(() => {
  active = false
})

async function submit() {
  if (busy.value || unknown.value) return
  busy.value = true
  error.value = ''
  try {
    await session.request<StorageElement>(element ? `/elements/${element.id}/rename` : '/folders', {
      method: 'POST',
      body: JSON.stringify(
        element ? { name: name.value } : { name: name.value, parentId, space: 'personal' },
      ),
    })
    if (!active) return
    emit('changed')
    emit('close')
  } catch (cause) {
    if (!active) return
    const failure = mutationFailure(cause)
    error.value = failure.message
    unknown.value = failure.unknown
    if (failure.unknown) emit('changed')
  } finally {
    if (active) busy.value = false
  }
}
</script>

<template>
  <UModal
    :open="true"
    :title="element ? 'Renommer l’élément' : 'Nouveau dossier'"
    :description="
      element
        ? `Nouveau nom pour « ${element.name} ».`
        : 'Créer un dossier dans l’emplacement courant.'
    "
    :dismissible="!busy"
    :close="!busy"
    @update:open="!$event && !busy && emit('close')"
  >
    <template #body>
      <form class="space-y-5" @submit.prevent="submit">
        <UFormField label="Nom" name="name">
          <UInput
            id="element-name"
            v-model="name"
            autofocus
            required
            :disabled="busy || unknown"
            class="w-full"
          />
        </UFormField>
        <p v-if="error" role="alert" class="text-sm text-error">{{ error }}</p>
        <div class="flex flex-wrap justify-end gap-3">
          <UButton color="neutral" variant="outline" :disabled="busy" @click="emit('close')">{{
            unknown ? 'Fermer' : 'Annuler'
          }}</UButton>
          <UButton v-if="!unknown" type="submit" :loading="busy">{{
            element ? 'Enregistrer' : 'Créer'
          }}</UButton>
        </div>
      </form>
    </template>
  </UModal>
</template>
