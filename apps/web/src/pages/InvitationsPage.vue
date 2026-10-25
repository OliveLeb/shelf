<script setup lang="ts">
import { onMounted, onUnmounted, shallowRef } from 'vue'
import { ApiError } from '../api'
import AppHeader from '../components/AppHeader.vue'
import { useSession } from '../stores/session'

interface Invitation {
  id: string
  email: string
  createdAt: string
  expiresAt: string
  revoked: boolean
  consumed: boolean
}
interface InvitationLink {
  id: string
  email: string
  expiresAt: string
  link: string
}

const session = useSession()
const email = shallowRef('')
const invitations = shallowRef<Invitation[]>([])
const issued = shallowRef<InvitationLink | null>(null)
const loading = shallowRef(true)
const pending = shallowRef(false)
const listError = shallowRef('')
const actionError = shallowRef('')
const copyMessage = shallowRef('')
const controller = new AbortController()
const date = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium', timeStyle: 'short' })
const statusLabels = {
  consumed: 'Utilisée',
  revoked: 'Révoquée',
  expired: 'Expirée',
  pending: 'En attente',
}

function state(invitation: Invitation) {
  if (invitation.consumed) return 'consumed'
  if (invitation.revoked) return 'revoked'
  if (Date.parse(invitation.expiresAt) <= Date.now()) return 'expired'
  return 'pending'
}

async function load() {
  if (controller.signal.aborted) return
  loading.value = true
  listError.value = ''
  try {
    invitations.value = await session.request<Invitation[]>('/admin/invitations', {
      signal: controller.signal,
    })
  } catch (cause) {
    if (!controller.signal.aborted)
      listError.value =
        cause instanceof ApiError && cause.status === 403
          ? 'L’administration est réservée aux administrateurs.'
          : 'Impossible de charger les invitations. Réessayez explicitement.'
  } finally {
    loading.value = false
  }
}

async function create() {
  if (pending.value) return
  pending.value = true
  issued.value = null
  actionError.value = ''
  copyMessage.value = ''
  try {
    issued.value = await session.request<InvitationLink>('/admin/invitations', {
      method: 'POST',
      signal: controller.signal,
      body: JSON.stringify({ email: email.value }),
    })
    email.value = ''
    await load()
  } catch (cause) {
    if (!controller.signal.aborted)
      actionError.value =
        cause instanceof ApiError && cause.status === 409
          ? 'Cet email est déjà associé à un compte.'
          : cause instanceof ApiError && cause.code === 'invalid_email'
            ? 'Saisissez une adresse email valide.'
            : 'Impossible de confirmer la création. Actualisez la liste avant de réessayer : une nouvelle invitation révoque les précédentes pour cet email.'
  } finally {
    pending.value = false
  }
}

async function revoke(invitation: Invitation) {
  if (pending.value) return
  pending.value = true
  actionError.value = ''
  try {
    await session.request<void>(`/admin/invitations/${invitation.id}`, {
      method: 'DELETE',
      signal: controller.signal,
    })
    if (issued.value?.id === invitation.id) issued.value = null
    await load()
  } catch {
    if (!controller.signal.aborted)
      actionError.value =
        'Impossible de confirmer la révocation. Actualisez la liste avant de réessayer.'
  } finally {
    pending.value = false
  }
}

async function copy() {
  if (!issued.value) return
  try {
    await navigator.clipboard.writeText(issued.value.link)
    copyMessage.value = 'Lien copié. Transmettez-le uniquement à la personne invitée.'
  } catch {
    copyMessage.value =
      'Copie impossible. Sélectionnez le lien ci-dessus et copiez-le manuellement.'
  }
}

onMounted(load)
onUnmounted(() => {
  controller.abort()
  issued.value = null
})
</script>

<template>
  <div class="min-h-dvh bg-muted/20">
    <AppHeader />
    <main class="mx-auto max-w-5xl space-y-8 px-5 py-10 sm:px-8 sm:py-14">
      <div>
        <p class="mb-2 text-xs font-medium uppercase tracking-widest text-primary">
          Administration
        </p>
        <h1 class="text-2xl font-semibold tracking-tight sm:text-3xl">Invitations</h1>
        <p class="mt-2 text-sm text-muted">
          Invitez une personne à rejoindre Shelf. Aucun email n’est envoyé automatiquement.
        </p>
      </div>
      <UCard>
        <h2 class="mb-2 font-semibold">Inviter une personne</h2>
        <p class="mb-5 text-sm text-muted">
          Le lien est valable 7 jours, à usage unique. Une nouvelle invitation au même email révoque
          les précédentes.
        </p>
        <form
          class="flex flex-col items-start gap-4 sm:flex-row sm:items-end"
          :aria-busy="pending"
          @submit.prevent="create"
        >
          <UFormField
            label="Email de la personne invitée"
            name="email"
            required
            class="w-full sm:max-w-sm"
          >
            <UInput
              v-model="email"
              name="email"
              type="email"
              autocomplete="off"
              :maxlength="320"
              required
              class="w-full"
              size="lg"
              :disabled="pending"
            />
          </UFormField>
          <UButton
            type="submit"
            size="lg"
            :loading="pending"
            :disabled="pending"
            icon="i-lucide-user-plus"
            >Créer une invitation</UButton
          >
        </form>
        <section
          v-if="issued"
          aria-label="Lien de la nouvelle invitation"
          class="mt-6 space-y-4 rounded-lg border border-primary/30 bg-primary/5 p-4"
        >
          <h3 class="font-medium">Invitation créée pour {{ issued.email }}</h3>
          <p class="text-sm leading-relaxed text-muted">
            Copiez ce lien secret maintenant : il ne sera plus récupérable après cet affichage
            initial. Transmettez-le manuellement à la personne invitée.
          </p>
          <UFormField label="Lien secret à transmettre" name="invitation-link">
            <UTextarea
              :model-value="issued.link"
              name="invitation-link"
              readonly
              :rows="3"
              class="w-full"
            />
          </UFormField>
          <div class="flex flex-wrap gap-3">
            <UButton icon="i-lucide-copy" @click="copy">Copier le lien</UButton>
            <UButton color="neutral" variant="outline" @click="issued = null"
              >Masquer le lien</UButton
            >
          </div>
          <p v-if="copyMessage" role="status" class="text-sm">{{ copyMessage }}</p>
        </section>
        <p v-if="actionError" role="alert" class="mt-5 text-sm text-error">{{ actionError }}</p>
      </UCard>
      <section aria-labelledby="invitation-list-title" :aria-busy="loading">
        <div class="mb-4 flex items-center justify-between gap-4">
          <h2 id="invitation-list-title" class="font-semibold">Toutes les invitations</h2>
          <UButton
            color="neutral"
            variant="outline"
            :disabled="loading || pending"
            icon="i-lucide-refresh-cw"
            @click="load"
            >Actualiser</UButton
          >
        </div>
        <p class="mb-4 text-sm text-muted">
          Cette liste contient uniquement les métadonnées, jamais les liens secrets.
        </p>
        <p v-if="loading" role="status" class="py-8 text-sm text-muted">
          Chargement des invitations…
        </p>
        <div
          v-else-if="listError"
          class="space-y-4 rounded-xl border border-default bg-default p-6"
        >
          <p role="alert" class="text-sm text-error">{{ listError }}</p>
          <UButton color="neutral" variant="outline" @click="load">Réessayer</UButton>
        </div>
        <p
          v-else-if="invitations.length === 0"
          role="status"
          class="rounded-xl border border-default bg-default p-8 text-center text-sm text-muted"
        >
          Aucune invitation pour le moment.
        </p>
        <ul
          v-else
          class="divide-y divide-default overflow-hidden rounded-xl border border-default bg-default"
        >
          <li
            v-for="invitation in invitations"
            :key="invitation.id"
            class="flex flex-col gap-4 p-5 sm:flex-row sm:items-center sm:justify-between"
          >
            <div class="min-w-0 space-y-2">
              <p class="break-words font-medium">{{ invitation.email }}</p>
              <p class="text-xs leading-relaxed text-muted">
                Créée le {{ date.format(new Date(invitation.createdAt)) }} · Expire le
                {{ date.format(new Date(invitation.expiresAt)) }}
              </p>
            </div>
            <div class="flex shrink-0 items-center gap-4">
              <UBadge color="neutral" variant="subtle">{{
                statusLabels[state(invitation)]
              }}</UBadge>
              <UButton
                v-if="state(invitation) === 'pending'"
                color="error"
                variant="outline"
                :disabled="pending"
                @click="revoke(invitation)"
                >Révoquer<span class="sr-only">
                  l’invitation pour {{ invitation.email }}</span
                ></UButton
              >
            </div>
          </li>
        </ul>
      </section>
    </main>
  </div>
</template>
