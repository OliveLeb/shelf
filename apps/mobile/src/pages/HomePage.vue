<script setup lang="ts">
import { Capacitor } from '@capacitor/core'
import { ShelfBrand } from '@shelf/ui'

const platform = Capacitor.getPlatform()
</script>

<template>
  <main class="flex min-h-screen flex-col items-center justify-center gap-6 p-6">
    <ShelfBrand tagline="Application mobile en préparation" />
    <UBadge :label="`Plateforme : ${platform}`" color="neutral" variant="subtle" />
  </main>
</template>
