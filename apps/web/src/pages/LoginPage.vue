<script setup lang="ts">
import { ShelfBrand } from '@shelf/ui'
import { shallowRef } from 'vue'
import { ApiError } from '../api'
import { useSession } from '../stores/session'

const session = useSession()
const identifier = shallowRef('')
const password = shallowRef('')
const pending = shallowRef(false)
const error = shallowRef('')

async function submit() {
  if (pending.value) return
  pending.value = true
  error.value = ''
  try {
    await session.login(identifier.value, password.value)
  } catch (cause) {
    error.value =
      cause instanceof ApiError && cause.status === 401
        ? 'Identifiant ou mot de passe incorrect.'
        : cause instanceof ApiError && cause.status === 429
          ? 'Trop de tentatives. Patientez un instant avant de réessayer.'
          : 'Connexion impossible pour le moment. Veuillez réessayer.'
  } finally {
    password.value = ''
    pending.value = false
  }
}
</script>

<template>
  <main class="flex min-h-dvh flex-col items-center justify-center gap-10 bg-muted/30 px-5 py-12">
    <ShelfBrand tagline="Vos fichiers, chez vous." />
    <UCard class="w-full max-w-md">
      <div class="mb-8">
        <h1 class="text-2xl font-semibold tracking-tight">Connexion</h1>
        <p class="mt-2 text-sm text-muted">Retrouvez votre espace personnel.</p>
      </div>
      <form class="space-y-5" :aria-busy="pending" @submit.prevent="submit">
        <UFormField label="Nom d’utilisateur ou email" name="identifier" required>
          <UInput
            v-model="identifier"
            name="identifier"
            autocomplete="username"
            :maxlength="320"
            required
            class="w-full"
            size="lg"
            :disabled="pending"
          />
        </UFormField>
        <UFormField label="Mot de passe" name="password" required>
          <UInput
            v-model="password"
            name="password"
            type="password"
            autocomplete="current-password"
            :maxlength="1024"
            required
            class="w-full"
            size="lg"
            :disabled="pending"
          />
        </UFormField>
        <p v-if="error" role="alert" class="text-sm text-error">{{ error }}</p>
        <UButton type="submit" block size="lg" :loading="pending" :disabled="pending"
          >Se connecter</UButton
        >
      </form>
      <template #footer>
        <p class="text-xs leading-relaxed text-muted">
          Votre session reste ouverte sur ce navigateur. Sur un appareil partagé, pensez à vous
          déconnecter.
        </p>
      </template>
    </UCard>
    <p class="text-xs text-muted">Un espace privé, accessible sur invitation.</p>
  </main>
</template>
