<script setup lang="ts">
import { BellOff, FileText, LoaderCircle, Paperclip, Send, X } from 'lucide-vue-next'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { api, errorText } from '@/api/client'
import type { Message } from '@/api/types'
import { draftProblem, emptyDraft, maxFiles, maxTitleLength, parseTags, publishForm } from '@/lib/compose'
import { splitTags } from '@/lib/emojis'
import { fileSize, priorityLabels } from '@/lib/format'
import { useTopicsStore } from '@/stores/topics'

interface Picked {
  file: File
  preview: string | null
}

const route = useRoute()
const topics = useTopicsStore()

const draft = ref(emptyDraft(typeof route.query.topic === 'string' ? route.query.topic : ''))
const picked = ref<Picked[]>([])
const sending = ref(false)
const error = ref('')
const sent = ref<Message | null>(null)
const dragging = ref(false)
const fileInput = ref<HTMLInputElement>()

// One key per draft: sending again after a timeout stores the message only once.
let idempotencyKey = crypto.randomUUID()
watch(draft, () => (error.value = ''), { deep: true })

const files = computed(() => picked.value.map((p) => p.file))
const problem = computed(() => draftProblem(draft.value, files.value))
const topic = computed(() => topics.byName(draft.value.topic.trim()))
const tagPreview = computed(() => splitTags(parseTags(draft.value.tags)))

function addFiles(list: FileList | null | undefined) {
  for (const file of Array.from(list ?? [])) {
    if (picked.value.length >= maxFiles) break
    picked.value.push({ file, preview: file.type.startsWith('image/') ? URL.createObjectURL(file) : null })
  }
  if (fileInput.value) fileInput.value.value = ''
}

function removeFile(index: number) {
  const [removed] = picked.value.splice(index, 1)
  if (removed?.preview) URL.revokeObjectURL(removed.preview)
}

function clearFiles() {
  for (const p of picked.value) if (p.preview) URL.revokeObjectURL(p.preview)
  picked.value = []
}
onBeforeUnmount(clearFiles)

function drop(event: DragEvent) {
  dragging.value = false
  addFiles(event.dataTransfer?.files)
}

/** Images pasted into the text (screenshots) become attachments. */
function paste(event: ClipboardEvent) {
  const images = Array.from(event.clipboardData?.files ?? []).filter((f) => f.type.startsWith('image/'))
  if (images.length === 0) return
  event.preventDefault()
  const transfer = new DataTransfer()
  images.forEach((f) => transfer.items.add(f))
  addFiles(transfer.files)
}

async function send() {
  if (problem.value) {
    error.value = problem.value
    return
  }
  sending.value = true
  error.value = ''
  sent.value = null
  try {
    sent.value = await api.publish(publishForm(draft.value, files.value), idempotencyKey)
    // Topic and priority stay for the next message, the rest starts empty.
    draft.value = { ...emptyDraft(draft.value.topic.trim()), priority: draft.value.priority }
    clearFiles()
    idempotencyKey = crypto.randomUUID()
  } catch (e) {
    error.value = errorText(e)
  } finally {
    sending.value = false
  }
}
</script>

<template>
  <div class="mx-auto max-w-2xl p-4">
    <h1 class="mb-4 text-lg font-semibold">Nachricht senden</h1>

    <div v-if="sent" class="mb-4 flex items-center gap-3 rounded-xl border border-emerald-300 bg-emerald-50 p-3 text-sm dark:border-emerald-500/40 dark:bg-emerald-500/10" role="status">
      <span class="flex-1">
        Gesendet an <strong>{{ topics.label(sent.topic) }}</strong>.
        <template v-if="topics.byName(sent.topic)?.muted">Das Topic ist stumm, es gab keinen Push.</template>
      </span>
      <RouterLink :to="{ name: 'topic', params: { topic: sent.topic } }" class="btn">Zum Topic</RouterLink>
      <button class="btn-icon" aria-label="Hinweis schließen" @click="sent = null"><X class="size-4" /></button>
    </div>

    <form class="card space-y-4 p-4" novalidate @submit.prevent="send">
      <div class="grid gap-4 sm:grid-cols-[1fr_auto]">
        <label class="block text-sm">
          <span class="mb-1 block text-xs text-stone-500">Topic</span>
          <input
            v-model="draft.topic"
            class="input w-full font-mono"
            list="send-topics"
            required
            autocomplete="off"
            autocapitalize="none"
            spellcheck="false"
            placeholder="garage"
          />
          <datalist id="send-topics">
            <option v-for="t in topics.topics" :key="t.name" :value="t.name">{{ t.displayName ?? t.name }}</option>
          </datalist>
          <span v-if="topic?.muted" class="mt-1 flex items-center gap-1 text-xs text-amber-700 dark:text-amber-400">
            <BellOff class="size-3.5" /> Stumm: wird gespeichert, aber ohne Push.
          </span>
          <span v-else-if="draft.topic.trim() && !topic" class="mt-1 block text-xs text-stone-500">Neues Topic, es wird beim Senden angelegt.</span>
        </label>

        <fieldset class="text-sm">
          <legend class="mb-1 text-xs text-stone-500">Priorität</legend>
          <div class="inline-flex overflow-hidden rounded-lg border border-stone-300 dark:border-stone-700">
            <label
              v-for="p in [1, 2, 3, 4, 5]"
              :key="p"
              class="cursor-pointer border-stone-300 px-2.5 py-1.5 text-sm not-last:border-r has-checked:bg-amber-600 has-checked:text-white has-focus-visible:ring-2 has-focus-visible:ring-amber-500/50 dark:border-stone-700"
              :title="priorityLabels[p]"
            >
              <input v-model.number="draft.priority" type="radio" name="priority" :value="p" class="sr-only" :aria-label="`${p} – ${priorityLabels[p]}`" />
              {{ p }}
            </label>
          </div>
          <span class="mt-1 block text-xs text-stone-500">{{ priorityLabels[draft.priority] }}<template v-if="draft.priority === 5">, durchbricht Fokus</template></span>
        </fieldset>
      </div>

      <label class="block text-sm">
        <span class="mb-1 block text-xs text-stone-500">Titel <span class="text-stone-400">(optional)</span></span>
        <input v-model="draft.title" class="input w-full" :maxlength="maxTitleLength" placeholder="Garagentor offen" />
      </label>

      <label class="block text-sm">
        <span class="mb-1 block text-xs text-stone-500">Text <span class="text-stone-400">(Markdown: **fett**, *kursiv*, `Code`, Links)</span></span>
        <textarea v-model="draft.message" class="input min-h-32 w-full resize-y" rows="5" placeholder="Seit 10 Minuten offen." @paste="paste" />
      </label>

      <div class="grid gap-4 sm:grid-cols-2">
        <label class="block text-sm">
          <span class="mb-1 block text-xs text-stone-500">Tags <span class="text-stone-400">(mit Komma oder Leerzeichen)</span></span>
          <input v-model="draft.tags" class="input w-full" autocapitalize="none" spellcheck="false" placeholder="warning, garage" />
          <span v-if="tagPreview.emojis || tagPreview.rest.length" class="mt-1 flex flex-wrap items-center gap-1 text-xs">
            <span v-if="tagPreview.emojis" class="text-base">{{ tagPreview.emojis }}</span>
            <span v-for="t in tagPreview.rest" :key="t" class="chip">{{ t }}</span>
          </span>
        </label>
        <label class="block text-sm">
          <span class="mb-1 block text-xs text-stone-500">Link beim Antippen <span class="text-stone-400">(optional)</span></span>
          <input v-model="draft.click" type="url" class="input w-full" placeholder="https://…" />
        </label>
      </div>

      <div
        class="rounded-lg border border-dashed p-3 text-sm transition"
        :class="dragging ? 'border-amber-500 bg-amber-50 dark:bg-amber-500/10' : 'border-stone-300 dark:border-stone-700'"
        @dragover.prevent="dragging = true"
        @dragleave="dragging = false"
        @drop.prevent="drop"
      >
        <div class="flex flex-wrap items-center gap-2">
          <button type="button" class="btn" :disabled="picked.length >= maxFiles" @click="fileInput?.click()">
            <Paperclip class="size-4" /> Anhänge
          </button>
          <span class="text-xs text-stone-500">Bilder oder Dateien hierher ziehen oder in den Text einfügen, höchstens {{ maxFiles }}.</span>
          <input ref="fileInput" type="file" multiple class="hidden" @change="addFiles(($event.target as HTMLInputElement).files)" />
        </div>
        <ul v-if="picked.length" class="mt-3 flex flex-wrap gap-2">
          <li v-for="(p, i) in picked" :key="i" class="relative">
            <img v-if="p.preview" :src="p.preview" :alt="p.file.name" class="size-20 rounded-lg object-cover" />
            <div v-else class="flex h-20 w-32 flex-col justify-center gap-1 rounded-lg bg-stone-100 p-2 text-xs dark:bg-stone-800">
              <FileText class="size-4 text-stone-500" />
              <span class="truncate">{{ p.file.name }}</span>
              <span class="text-stone-500">{{ fileSize(p.file.size) }}</span>
            </div>
            <button
              type="button"
              class="absolute -top-1.5 -right-1.5 flex size-5 items-center justify-center rounded-full bg-stone-800 text-white shadow hover:bg-red-600"
              :aria-label="`${p.file.name} entfernen`"
              @click="removeFile(i)"
            >
              <X class="size-3" />
            </button>
          </li>
        </ul>
      </div>

      <div class="flex items-center gap-3">
        <p v-if="error" class="flex-1 text-sm text-red-600" role="alert">{{ error }}</p>
        <span v-else class="flex-1" />
        <button class="btn-primary" :disabled="sending">
          <LoaderCircle v-if="sending" class="size-4 animate-spin" />
          <Send v-else class="size-4" />
          Senden
        </button>
      </div>
    </form>
  </div>
</template>
