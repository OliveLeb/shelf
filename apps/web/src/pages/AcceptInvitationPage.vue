<script setup lang="ts">
import { ShelfBrand } from '@shelf/ui'
import { shallowRef, watch } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { api, ApiError } from '../api'
import { useSession, type Account } from '../stores/session'

const router = useRouter()
const route = useRoute()
const session = useSession()
// Keep the secret only in this page's memory, not in browser history or a store.
let token = ''
const username = shallowRef('')
const password = shallowRef('')
const pending = shallowRef(false)
const created = shallowRef(false)
const rejected = shallowRef(true)
const error = shallowRef('')

// A second link can navigate only its fragment, reusing this route component.
onBeforeRouteUpdate((to) => (pending.value && to.hash ? false : undefined))
onBeforeRouteLeave(() => (pending.value ? false : undefined))
watch(
  () => route.hash,
  (hash) => {
    if (!hash) return
    token = new URLSearchParams(hash.slice(1)).get('token') ?? ''
    username.value = ''
    password.value = ''
    created.value = false
    rejected.value = !/^[a-fA-F0-9]{64}$/.test(token)
    error.value = ''
    void router.replace({ path: route.path, query: route.query, hash: '' })
  },
  { immediate: true },
)

async function submit() {
  if (pending.value || created.value || rejected.value) return
  pending.value = true
  error.value = ''
  try {
    const result = await api<Account>('/auth/web/register', {
      method: 'POST',
      body: JSON.stringify({ token, username: username.value, password: password.value }),
    })
    // Registration has committed. A later login failure must never offer it again.
    created.value = true
    token = ''
    try {
      await session.login(result.value.username, password.value, result.version)
      pending.value = false
      await router.replace('/')
    } catch {
      error.value =
        'Votre compte est créé, mais la connexion automatique n’a pas pu être confirmée. Connectez-vous pour continuer ; ne recommencez pas l’inscription.'
    }
  } catch (cause) {
    if (cause instanceof ApiError && cause.code === 'invalid_token') {
      rejected.value = true
      token = ''
    } else {
      error.value =
        cause instanceof ApiError && cause.status === 409
          ? 'Ce nom d’utilisateur est déjà utilisé. Choisissez-en un autre.'
          : cause instanceof ApiError && cause.code === 'invalid_username'
            ? 'Choisissez un nom de 1 à 100 caractères, sans @ ni caractère de contrôle.'
            : cause instanceof ApiError && cause.code === 'invalid_password'
              ? 'Le mot de passe doit contenir entre 12 et 1 024 caractères.'
              : cause instanceof ApiError && cause.status === 429
                ? 'Trop de tentatives. Patientez avant de réessayer.'
                : 'Impossible de confirmer la création du compte. Si la requête a abouti, essayez de vous connecter. Sinon, réessayez explicitement avec ce lien.'
    }
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
      <h1 class="text-2xl font-semibold tracking-tight">Rejoindre Shelf</h1>
      <div v-if="created" class="mt-5 space-y-5">
        <p role="status" class="font-medium">Votre compte est créé.</p>
        <p v-if="error" role="alert" class="text-sm text-muted">{{ error }}</p>
        <UButton to="/connexion" block>Se connecter</UButton>
      </div>
      <div v-else-if="rejected" class="mt-5 space-y-5">
        <p role="alert" class="text-sm text-error">
          Ce lien d’invitation n’est pas utilisable. Il peut être invalide, expiré, révoqué ou déjà
          utilisé. Demandez un nouveau lien à votre administrateur.
        </p>
        <UButton to="/connexion" color="neutral" variant="outline">Aller à la connexion</UButton>
      </div>
      <form v-else class="mt-5 space-y-5" :aria-busy="pending" @submit.prevent="submit">
        <p class="text-sm leading-relaxed text-muted">
          Choisissez vos identifiants. L’email de votre compte est celui fixé par l’invitation ; il
          ne peut pas être modifié ici.
        </p>
        <p v-if="session.status === 'authenticated' && session.account" class="text-sm text-muted">
          Vous êtes connecté en tant que {{ session.account.username }}. La création et la connexion
          au nouveau compte remplaceront cette session dans ce navigateur.
        </p>
        <UFormField label="Nom d’utilisateur" name="username" required>
          <UInput
            v-model="username"
            name="username"
            autocomplete="username"
            :maxlength="100"
            required
            size="lg"
            class="w-full"
            :disabled="pending"
          />
        </UFormField>
        <UFormField
          label="Mot de passe"
          name="password"
          description="Au moins 12 caractères."
          required
        >
          <UInput
            v-model="password"
            name="password"
            type="password"
            autocomplete="new-password"
            :minlength="12"
            :maxlength="1024"
            required
            size="lg"
            class="w-full"
            :disabled="pending"
          />
        </UFormField>
        <p v-if="error" role="alert" class="text-sm text-error">{{ error }}</p>
        <UButton type="submit" block size="lg" :loading="pending" :disabled="pending"
          >Créer mon compte</UButton
        >
        <UButton to="/connexion" color="neutral" variant="link" block :disabled="pending"
          >Déjà un compte ? Se connecter</UButton
        >
      </form>
    </UCard>
    <p class="text-xs text-muted">Un espace privé, accessible sur invitation.</p>
  </main>
</template>
