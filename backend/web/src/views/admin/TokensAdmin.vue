<script setup lang="ts">
import { Copy, KeyRound, LoaderCircle, Trash2 } from 'lucide-vue-next'
import { onMounted, ref } from 'vue'
import { api, errorText } from '@/api/client'
import type { CreatedPublishToken, PublishToken } from '@/api/types'
import { shortTime } from '@/lib/format'

const tokens = ref<PublishToken[]>([])
const loading = ref(true)
const error = ref('')

const name = ref('')
const topics = ref('')
const creating = ref(false)
const created = ref<CreatedPublishToken | null>(null)
const copied = ref(false)

async function load() {
  try {
    tokens.value = await api.publishTokens()
  } catch (e) {
    error.value = errorText(e)
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function create() {
  creating.value = true
  error.value = ''
  copied.value = false
  try {
    const list = topics.value.split(',').map((t) => t.trim()).filter(Boolean)
    created.value = await api.createPublishToken(name.value.trim(), list)
    name.value = ''
    topics.value = ''
    await load()
  } catch (e) {
    error.value = errorText(e)
  } finally {
    creating.value = false
  }
}

async function copy() {
  if (!created.value) return
  await navigator.clipboard.writeText(created.value.token)
  copied.value = true
}

async function remove(token: PublishToken) {
  if (!confirm(`Token „${token.name}“ löschen? Sender damit werden ab sofort abgewiesen.`)) return
  try {
    await api.deletePublishToken(token.id)
    tokens.value = tokens.value.filter((t) => t.id !== token.id)
    if (created.value?.id === token.id) created.value = null
  } catch (e) {
    error.value = errorText(e)
  }
}
</script>

<template>
  <p class="mb-4 text-sm text-stone-600 dark:text-stone-400">
    Sender (Skripte, Dienste, Grafana) schicken mit <code>Authorization: Bearer ob_…</code>. Ein Token darf nur an seine Topics
    senden, mit <code>*</code> an alle. Der Schlüssel erscheint nur einmal, gespeichert wird nur sein Hash.
  </p>

  <form class="card mb-4 flex flex-wrap items-end gap-3 p-4" @submit.prevent="create">
    <label class="text-sm">
      <span class="mb-1 block text-xs text-stone-500">Name</span>
      <input v-model="name" class="input w-44" required maxlength="100" placeholder="fil-o-mat" />
    </label>
    <label class="min-w-48 flex-1 text-sm">
      <span class="mb-1 block text-xs text-stone-500">Topics, mit Komma getrennt</span>
      <input v-model="topics" class="input w-full" required placeholder="filomat, kamera oder *" />
    </label>
    <button class="btn-primary" :disabled="creating">
      <LoaderCircle v-if="creating" class="size-4 animate-spin" />
      <KeyRound v-else class="size-4" />
      Anlegen
    </button>
  </form>

  <div v-if="created" class="mb-4 rounded-xl border border-amber-300 bg-amber-50 p-4 dark:border-amber-500/40 dark:bg-amber-500/10" role="status">
    <p class="mb-2 text-sm font-medium">Token „{{ created.name }}“ – jetzt kopieren, er wird nicht noch einmal angezeigt:</p>
    <div class="flex gap-2">
      <code class="input flex-1 overflow-x-auto font-mono text-xs whitespace-nowrap select-all">{{ created.token }}</code>
      <button class="btn" @click="copy"><Copy class="size-4" /> {{ copied ? 'Kopiert' : 'Kopieren' }}</button>
    </div>
  </div>

  <p v-if="error" class="mb-3 text-sm text-red-600" role="alert">{{ error }}</p>
  <LoaderCircle v-if="loading" class="mx-auto size-5 animate-spin text-stone-400" />
  <p v-else-if="tokens.length === 0" class="card p-6 text-center text-sm text-stone-500">Noch keine Publish-Tokens.</p>
  <ul v-else class="card divide-y divide-stone-200 dark:divide-stone-800">
    <li v-for="token in tokens" :key="token.id" class="flex items-center gap-3 p-3">
      <div class="min-w-0 flex-1">
        <p class="font-medium">{{ token.name }}</p>
        <p class="text-xs text-stone-500">
          <span v-for="t in token.topics" :key="t" class="chip mr-1 font-mono">{{ t }}</span>
          angelegt {{ shortTime(token.createdAt) }} ·
          {{ token.lastUsedAt ? `zuletzt benutzt ${shortTime(token.lastUsedAt)}` : 'noch nie benutzt' }}
        </p>
      </div>
      <button class="btn-icon hover:!text-red-600" :title="`Token „${token.name}“ löschen`" @click="remove(token)">
        <Trash2 class="size-4" />
      </button>
    </li>
  </ul>
</template>
