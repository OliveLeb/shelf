<script setup lang="ts">
import { computed, shallowRef } from 'vue'
import { useTransfers, type Transfer } from '../stores/transfers'
import UploadConflictDialog from './UploadConflictDialog.vue'

const transfers = useTransfers()
const expanded = shallowRef(true)
const labels: Record<Transfer['status'], string> = {
  queued: 'En attente',
  declaring: 'Préparation',
  uploading: 'Envoi',
  finalizing: 'Finalisation',
  completed: 'Terminé',
  error: 'Erreur',
  conflict: 'Conflit de nom',
  skipped: 'Ignoré',
}
const conflict = computed(() => transfers.items.find((item) => item.status === 'conflict'))
const batches = computed(() => {
  const groups = new Map<string, Transfer[]>()
  for (const item of transfers.items) {
    const group = groups.get(item.batchId) ?? []
    group.push(item)
    groups.set(item.batchId, group)
  }
  return Array.from(groups, ([id, items]) => ({
    id,
    items,
    completed: items.filter((item) => item.status === 'completed').length,
    errors: items.filter((item) => item.status === 'error').length,
    skipped: items.filter((item) => item.status === 'skipped').length,
    pending: items.filter((item) => !['completed', 'error', 'skipped'].includes(item.status))
      .length,
  }))
})
const bytes = new Intl.NumberFormat('fr-FR', {
  style: 'unit',
  unit: 'byte',
  notation: 'compact',
  maximumFractionDigits: 1,
})
</script>

<template>
  <aside
    v-if="transfers.items.length"
    id="transfers"
    aria-label="Suivi des transferts"
    class="border-t border-default bg-default"
  >
    <div class="mx-auto max-w-5xl px-5 py-4 sm:px-8">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="font-semibold">
          Transferts <span class="text-muted">({{ transfers.items.length }})</span>
        </h2>
        <div class="flex gap-2">
          <UButton
            v-if="!transfers.active"
            color="neutral"
            variant="ghost"
            size="sm"
            @click="transfers.clear"
            >Effacer le suivi</UButton
          >
          <UButton
            color="neutral"
            variant="outline"
            size="sm"
            v-bind="{ 'aria-expanded': expanded, 'aria-controls': 'transfer-details' }"
            @click="expanded = !expanded"
            >{{ expanded ? 'Réduire' : 'Afficher le suivi' }}</UButton
          >
        </div>
      </div>
      <div
        v-if="expanded"
        id="transfer-details"
        role="region"
        aria-label="Détail des lots"
        tabindex="0"
        class="mt-4 max-h-64 space-y-5 overflow-y-auto"
      >
        <section
          v-for="(batch, index) in batches"
          :key="batch.id"
          class="space-y-4"
          :aria-label="`Lot ${index + 1}`"
        >
          <div class="border-b border-default pb-2 text-sm">
            <h3 class="font-semibold">Lot {{ index + 1 }}</h3>
            <p aria-live="polite" class="text-xs text-muted">
              Réussites : {{ batch.completed }} · Échecs : {{ batch.errors }} · Ignorés :
              {{ batch.skipped }} · En cours : {{ batch.pending }}
            </p>
          </div>
          <article v-for="item in batch.items" :key="item.id" class="space-y-2">
            <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 text-sm">
              <h4 class="min-w-0 break-all font-medium">{{ item.name }}</h4>
              <span
                role="status"
                :class="
                  item.status === 'error'
                    ? 'text-error'
                    : item.status === 'completed'
                      ? 'text-success'
                      : 'text-primary'
                "
                >{{ labels[item.status] }}</span
              >
            </div>
            <p class="break-words text-xs text-muted">Destination : {{ item.destination.label }}</p>
            <div
              v-if="
                item.status === 'uploading' ||
                item.status === 'declaring' ||
                item.status === 'finalizing'
              "
              role="progressbar"
              :aria-label="`Envoi de ${item.name}`"
              :aria-valuenow="item.status === 'uploading' ? item.loaded : undefined"
              :aria-valuemin="0"
              :aria-valuemax="Math.max(item.size, 1)"
            >
              <UProgress
                :model-value="item.status === 'uploading' ? item.loaded : null"
                :max="Math.max(item.size, 1)"
                v-bind="{ 'aria-hidden': true }"
                size="sm"
              />
            </div>
            <p v-if="item.status === 'uploading'" class="text-xs text-muted">
              {{ bytes.format(item.loaded) }} / {{ bytes.format(item.size) }}
            </p>
            <p v-if="item.error" role="alert" class="text-sm text-error">{{ item.error }}</p>
          </article>
        </section>
      </div>
      <p class="mt-3 text-xs text-muted">
        Gardez cette application ouverte. Fermer ou recharger ne permet pas de reprendre les envois.
      </p>
    </div>
    <UploadConflictDialog
      v-if="conflict"
      :key="conflict.id"
      :name="conflict.name"
      :destination-label="conflict.destination.label"
      :batch-number="batches.findIndex((batch) => batch.id === conflict!.batchId) + 1"
      @decide="(choice, following) => transfers.resolveConflict(conflict!.id, choice, following)"
    />
  </aside>
</template>
