<script setup lang="ts">
import { computed, shallowRef, watch } from 'vue'
import { date, size, type ElementEntry } from '../storage'

const { entries, filter, atRoot } = defineProps<{
  entries: ElementEntry[]
  filter: string
  atRoot: boolean
}>()
const emit = defineEmits<{ inspect: [id: string]; clearFilter: [] }>()
const selected = shallowRef<string[]>([])
const visible = computed(() =>
  entries.filter(({ element }) =>
    element.name.toLocaleLowerCase('fr').includes(filter.toLocaleLowerCase('fr')),
  ),
)
const single = computed(() =>
  selected.value.length === 1
    ? visible.value.find(({ element }) => element.id === selected.value[0])
    : undefined,
)
const allSelected = computed(
  () => visible.value.length > 0 && selected.value.length === visible.value.length,
)

// Hidden selections must never turn into actions on names the user cannot see.
watch([() => entries, () => filter], () => {
  selected.value = []
})
function toggle(id: string, checked: boolean) {
  selected.value =
    checked === true ? [...selected.value, id] : selected.value.filter((value) => value !== id)
}
</script>

<template>
  <div
    v-if="entries.length === 0"
    role="status"
    class="flex min-h-64 flex-col items-center justify-center p-8 text-center"
  >
    <div class="mb-5 rounded-2xl bg-primary/10 p-4 text-primary">
      <UIcon name="i-lucide-folder" class="size-7" />
    </div>
    <h2 class="font-medium">
      {{ atRoot ? 'Votre espace est encore vide' : 'Ce dossier est vide' }}
    </h2>
    <p class="mt-2 text-sm text-muted">Vos fichiers et dossiers apparaîtront ici.</p>
  </div>
  <div
    v-else-if="visible.length === 0"
    role="status"
    class="flex min-h-64 flex-col items-center justify-center gap-4 p-8 text-center"
  >
    <UIcon name="i-lucide-search" class="size-7 text-muted" />
    <h2 class="font-medium">Aucun résultat dans ce dossier</h2>
    <p class="text-sm text-muted">Le filtre ne recherche pas dans les sous-dossiers.</p>
    <UButton variant="outline" color="neutral" @click="emit('clearFilter')"
      >Effacer le filtre</UButton
    >
  </div>
  <template v-else>
    <div
      v-if="selected.length"
      class="flex flex-wrap items-center gap-3 border-b border-default bg-primary/5 px-4 py-3"
    >
      <p role="status" class="mr-auto text-sm font-medium">
        {{ selected.length }}
        {{ selected.length === 1 ? 'élément sélectionné' : 'éléments sélectionnés' }}
      </p>
      <template v-if="single">
        <UButton
          v-if="single.element.kind === 'file'"
          size="sm"
          variant="outline"
          color="neutral"
          @click="emit('inspect', single.element.id)"
          >Informations</UButton
        >
        <UButton
          v-else
          size="sm"
          variant="outline"
          color="neutral"
          :to="`/dossiers/${single.element.id}`"
          >Ouvrir le dossier</UButton
        >
      </template>
      <UButton size="sm" variant="ghost" color="neutral" @click="selected = []"
        >Désélectionner</UButton
      >
      <p v-if="selected.length > 1" class="w-full text-xs text-muted">
        Pour télécharger, ouvrez les informations d’un fichier. Pas de téléchargement groupé.
      </p>
    </div>
    <table class="w-full table-fixed text-left text-sm">
      <caption class="sr-only">
        Fichiers et dossiers du dossier courant
      </caption>
      <thead class="border-b border-default bg-muted/40 text-xs text-muted">
        <tr>
          <th scope="col" class="w-16 px-2 py-1">
            <label class="flex size-11 cursor-pointer items-center justify-center">
              <input
                type="checkbox"
                :checked="allSelected"
                :indeterminate="selected.length > 0 && !allSelected"
                class="size-4 cursor-pointer accent-primary focus-visible:outline-2 focus-visible:outline-primary"
                aria-label="Sélectionner tous les éléments affichés"
                @change="
                  selected = ($event.target as HTMLInputElement).checked
                    ? visible.map((entry) => entry.element.id)
                    : []
                "
              />
            </label>
          </th>
          <th scope="col" class="px-2 py-3 font-medium">Nom</th>
          <th scope="col" class="w-24 px-3 py-3 text-right font-medium">Taille</th>
          <th scope="col" class="hidden w-40 px-4 py-3 font-medium sm:table-cell">Ajouté le</th>
        </tr>
      </thead>
      <tbody class="divide-y divide-default">
        <tr
          v-for="{ element } in visible"
          :key="element.id"
          :class="selected.includes(element.id) ? 'bg-primary/5' : 'hover:bg-muted/40'"
        >
          <td class="px-2 py-2">
            <label class="flex size-11 cursor-pointer items-center justify-center">
              <input
                type="checkbox"
                :checked="selected.includes(element.id)"
                class="size-4 cursor-pointer accent-primary focus-visible:outline-2 focus-visible:outline-primary"
                :aria-label="`Sélectionner ${element.name}`"
                @change="toggle(element.id, ($event.target as HTMLInputElement).checked)"
              />
            </label>
          </td>
          <td class="px-2 py-4">
            <div class="flex min-w-0 items-center gap-3">
              <UIcon
                :name="element.kind === 'folder' ? 'i-lucide-folder' : 'i-lucide-file'"
                class="hidden size-5 shrink-0 text-primary min-[420px]:block"
              />
              <div class="min-w-0">
                <RouterLink
                  v-if="element.kind === 'folder'"
                  :to="`/dossiers/${element.id}`"
                  class="block break-words font-medium hover:text-primary focus-visible:outline-2 focus-visible:outline-primary"
                  >{{ element.name }}<span class="sr-only"> (dossier)</span></RouterLink
                >
                <button
                  v-else
                  type="button"
                  class="block max-w-full break-words text-left font-medium hover:text-primary focus-visible:outline-2 focus-visible:outline-primary"
                  @click="emit('inspect', element.id)"
                >
                  {{ element.name }}<span class="sr-only"> (fichier)</span>
                </button>
                <p class="mt-1 text-xs text-muted sm:hidden">
                  {{ date.format(new Date(element.addedAt)) }}
                </p>
              </div>
            </div>
          </td>
          <td class="px-3 py-4 text-right text-muted">
            {{ element.size === null ? '—' : size.format(element.size) }}
          </td>
          <td class="hidden px-4 py-4 text-muted sm:table-cell">
            {{ date.format(new Date(element.addedAt)) }}
          </td>
        </tr>
      </tbody>
    </table>
  </template>
</template>
