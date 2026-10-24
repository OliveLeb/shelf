<script setup lang="ts">
const { tagline } = defineProps<{ tagline?: string }>()
</script>

<template>
  <div class="flex flex-col items-center gap-1">
    <span class="text-3xl font-semibold text-primary">Shelf</span>
    <span v-if="tagline" class="text-sm text-muted">{{ tagline }}</span>
  </div>
</template>
