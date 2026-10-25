<script setup lang="ts">
import UModal from '@nuxt/ui/components/Modal.vue'
import { shallowRef, useId } from 'vue'
import type { ConflictChoice } from '../stores/transfers'

const {
  name,
  destinationLabel,
  batchNumber = 1,
} = defineProps<{ name: string; destinationLabel: string; batchNumber?: number }>()
const emit = defineEmits<{
  decide: [choice: ConflictChoice, following: boolean]
}>()
const choice = shallowRef<ConflictChoice>('keepBoth')
const following = shallowRef(false)
const id = useId()
</script>

<template>
  <UModal
    :open="true"
    :close="false"
    :dismissible="false"
    title="Un élément porte déjà ce nom"
    :description="`Lot ${batchNumber} : « ${name} » existe dans ${destinationLabel}. Choisissez comment poursuivre cet envoi.`"
    :ui="{ description: 'break-words' }"
  >
    <template #body>
      <form :id="id" class="space-y-5" @submit.prevent="emit('decide', choice, following)">
        <fieldset class="space-y-3">
          <legend class="sr-only">Résoudre le conflit de nom</legend>
          <label
            class="flex cursor-pointer items-start gap-3 rounded-lg border border-default p-3 has-checked:border-primary has-checked:bg-primary/5"
          >
            <input
              v-model="choice"
              type="radio"
              :name="id"
              value="keepBoth"
              class="mt-1 accent-primary"
            />
            <span class="text-sm"
              ><span class="block font-medium">Garder les deux</span
              ><span class="text-muted"
                >Le serveur attribue un nouveau nom au fichier envoyé.</span
              ></span
            >
          </label>
          <label
            class="flex cursor-pointer items-start gap-3 rounded-lg border border-default p-3 has-checked:border-primary has-checked:bg-primary/5"
          >
            <input
              v-model="choice"
              type="radio"
              :name="id"
              value="replace"
              class="mt-1 accent-primary"
            />
            <span class="text-sm"
              ><span class="block font-medium">Remplacer</span
              ><span class="text-muted"
                >L’ancien contenu du fichier est conservé dans la corbeille. Un dossier ne peut pas
                être remplacé.</span
              ></span
            >
          </label>
          <label
            class="flex cursor-pointer items-start gap-3 rounded-lg border border-default p-3 has-checked:border-primary has-checked:bg-primary/5"
          >
            <input
              v-model="choice"
              type="radio"
              :name="id"
              value="skip"
              class="mt-1 accent-primary"
            />
            <span class="text-sm"
              ><span class="block font-medium">Ignorer</span
              ><span class="text-muted"
                >Ne pas envoyer ce fichier. L’élément existant reste inchangé.</span
              ></span
            >
          </label>
        </fieldset>
        <label class="flex cursor-pointer items-start gap-3 text-sm">
          <input v-model="following" type="checkbox" class="mt-1 accent-primary" />
          <span>Appliquer aux conflits suivants de ce lot</span>
        </label>
      </form>
    </template>
    <template #footer>
      <UButton type="submit" :form="id" class="ml-auto">Confirmer le choix</UButton>
    </template>
  </UModal>
</template>
