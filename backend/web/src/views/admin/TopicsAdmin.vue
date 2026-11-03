<script setup lang="ts">
import { Check, LoaderCircle } from 'lucide-vue-next'
import { reactive, ref, watch } from 'vue'
import { errorText } from '@/api/client'
import type { Topic, TopicPatch } from '@/api/types'
import TopicIcon from '@/components/TopicIcon.vue'
import { useTopicsStore } from '@/stores/topics'

const topics = useTopicsStore()

/** What the form shows per topic; texts empty instead of null. */
interface Draft {
  displayName: string
  icon: string
  color: string | null
  muted: boolean
  /** v-model of a number field gives a number, an empty one a string. */
  retentionDays: string | number
}

const drafts = reactive<Record<string, Draft>>({})
const saving = ref<string | null>(null)
const saved = ref<string | null>(null)
const errors = reactive<Record<string, string>>({})

function toDraft(topic: Topic): Draft {
  return {
    displayName: topic.displayName ?? '',
    icon: topic.icon ?? '',
    color: topic.color,
    muted: topic.muted,
    retentionDays: topic.retentionDays?.toString() ?? '',
  }
}

// New topics (they come with their first message) get a form; open edits of known ones stay.
watch(() => topics.topics, (list) => {
  for (const topic of list) drafts[topic.name] ??= toDraft(topic)
}, { immediate: true })

/** Only the fields that changed go to the server. */
function changes(topic: Topic, draft: Draft): TopicPatch {
  const patch: TopicPatch = {}
  const text = (value: string) => (value.trim() === '' ? null : value.trim())
  if (text(draft.displayName) !== topic.displayName) patch.displayName = text(draft.displayName)
  if (text(draft.icon) !== topic.icon) patch.icon = text(draft.icon)
  if (draft.color !== topic.color) patch.color = draft.color
  if (draft.muted !== topic.muted) patch.muted = draft.muted
  const daysText = String(draft.retentionDays).trim()
  const days = daysText === '' ? null : Number(daysText)
  if (days !== topic.retentionDays) patch.retentionDays = days
  return patch
}

async function save(topic: Topic) {
  const draft = drafts[topic.name]!
  saving.value = topic.name
  delete errors[topic.name]
  try {
    const updated = await topics.patch(topic.name, changes(topic, draft))
    drafts[topic.name] = toDraft(updated)
    saved.value = topic.name
    setTimeout(() => saved.value === topic.name && (saved.value = null), 2000)
  } catch (e) {
    errors[topic.name] = errorText(e)
  } finally {
    saving.value = null
  }
}

function isDirty(topic: Topic) {
  const draft = drafts[topic.name]
  return !!draft && Object.keys(changes(topic, draft)).length > 0
}
</script>

<template>
  <p class="mb-4 text-sm text-stone-600 dark:text-stone-400">
    Ein Topic entsteht mit seiner ersten Nachricht. Stumme Topics schicken keinen Push. Ohne Aufbewahrung bleiben Nachrichten für immer.
  </p>
  <p v-if="topics.loaded && topics.topics.length === 0" class="card p-6 text-center text-sm text-stone-500">Noch keine Topics.</p>

  <div class="space-y-3">
    <form
      v-for="topic in topics.topics"
      :key="topic.name"
      class="card grid gap-3 p-4 sm:grid-cols-[auto_1fr_1fr]"
      @submit.prevent="save(topic)"
    >
      <template v-if="drafts[topic.name]">
        <div class="flex items-center gap-3 sm:row-span-2">
          <TopicIcon :topic="{ name: topic.name, icon: drafts[topic.name]!.icon || null, color: drafts[topic.name]!.color }" />
          <div>
            <p class="font-mono text-sm font-semibold">{{ topic.name }}</p>
            <p class="text-xs text-stone-500">{{ topic.total }} Nachrichten, {{ topic.unread }} ungelesen</p>
          </div>
        </div>
        <label class="text-sm">
          <span class="mb-1 block text-xs text-stone-500">Anzeigename</span>
          <input v-model="drafts[topic.name]!.displayName" class="input w-full" maxlength="64" :placeholder="topic.name" />
        </label>
        <label class="text-sm">
          <span class="mb-1 block text-xs text-stone-500">Icon (Emoji oder Buchstabe)</span>
          <input v-model="drafts[topic.name]!.icon" class="input w-full" maxlength="32" placeholder="🏠" />
        </label>
        <div class="flex items-end gap-2 text-sm">
          <label>
            <span class="mb-1 block text-xs text-stone-500">Farbe</span>
            <input
              type="color"
              class="h-8 w-12 cursor-pointer rounded border border-stone-300 bg-transparent dark:border-stone-700"
              :value="drafts[topic.name]!.color ?? '#a8a29e'"
              @input="drafts[topic.name]!.color = ($event.target as HTMLInputElement).value"
            />
          </label>
          <button v-if="drafts[topic.name]!.color" type="button" class="btn py-1" @click="drafts[topic.name]!.color = null">keine</button>
        </div>
        <div class="flex flex-wrap items-end gap-4 text-sm">
          <label>
            <span class="mb-1 block text-xs text-stone-500">Aufbewahrung in Tagen</span>
            <input v-model="drafts[topic.name]!.retentionDays" type="number" min="1" max="36500" class="input w-28" placeholder="immer" />
          </label>
          <label class="inline-flex items-center gap-2 pb-1.5">
            <input v-model="drafts[topic.name]!.muted" type="checkbox" class="accent-amber-600" /> stumm
          </label>
          <button class="btn-primary ml-auto" :disabled="!isDirty(topic) || saving === topic.name">
            <LoaderCircle v-if="saving === topic.name" class="size-4 animate-spin" />
            <Check v-else-if="saved === topic.name" class="size-4" />
            Speichern
          </button>
        </div>
        <p v-if="errors[topic.name]" class="text-sm text-red-600 sm:col-span-3" role="alert">{{ errors[topic.name] }}</p>
      </template>
    </form>
  </div>
</template>
