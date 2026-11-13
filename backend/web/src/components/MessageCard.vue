<script setup lang="ts">
import { Check, ExternalLink, FileText, LoaderCircle, Trash2 } from 'lucide-vue-next'
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { errorText } from '@/api/client'
import type { Attachment, Message, MessageAction } from '@/api/types'
import { splitTags } from '@/lib/emojis'
import { fileSize, fullTime, priorityLabels, shortTime } from '@/lib/format'
import { safeHref } from '@/lib/markdown'
import { useMessagesStore } from '@/stores/messages'
import { useTopicsStore } from '@/stores/topics'
import MessageText from './MessageText.vue'
import TopicIcon from './TopicIcon.vue'

const props = defineProps<{ message: Message; showTopic: boolean; selecting?: boolean; selected?: boolean }>()
const emit = defineEmits<{ openImage: [attachment: Attachment]; toggle: [] }>()

const store = useMessagesStore()
const topics = useTopicsStore()

const tags = computed(() => splitTags(props.message.tags))
const images = computed(() => props.message.attachments.filter((a) => a.thumbnailUrl))
const files = computed(() => props.message.attachments.filter((a) => !a.thumbnailUrl))
const click = computed(() => (props.message.click ? safeHref(props.message.click) : null))
const unread = computed(() => !props.message.readAt)

const busy = ref<string | null>(null)
const error = ref('')

async function run(key: string, work: () => Promise<unknown>) {
  busy.value = key
  error.value = ''
  try {
    await work()
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = null
  }
}

function remove() {
  if (confirm('Nachricht mit ihren Anhängen löschen?')) void run('delete', () => store.remove(props.message))
}

function actionState(action: MessageAction) {
  if (action.lastStatus === null || !action.lastRunAt) return null
  const ok = action.lastStatus >= 200 && action.lastStatus < 300
  const text = action.lastStatus === 0 ? 'nicht erreicht' : `HTTP ${action.lastStatus}`
  return { ok, text: `${text}, ${shortTime(action.lastRunAt)}` }
}

const priorityClass: Record<number, string> = {
  1: 'bg-stone-100 text-stone-500 dark:bg-stone-800 dark:text-stone-400',
  2: 'bg-stone-100 text-stone-500 dark:bg-stone-800 dark:text-stone-400',
  4: 'bg-orange-100 text-orange-800 dark:bg-orange-500/15 dark:text-orange-300',
  5: 'bg-red-100 text-red-800 dark:bg-red-500/15 dark:text-red-300',
}
</script>

<template>
  <article
    class="card relative overflow-hidden p-4"
    :class="[unread && 'border-l-4 border-l-amber-500 dark:border-l-amber-500', selected && 'ring-2 ring-amber-500']"
    :aria-label="message.title ?? message.message.slice(0, 80)"
  >
    <header class="mb-1 flex items-start gap-2">
      <input
        v-if="selecting"
        type="checkbox"
        class="mt-1 size-4 shrink-0 accent-amber-600"
        :checked="selected"
        :aria-label="`Auswählen: ${message.title ?? message.message.slice(0, 80)}`"
        @change="emit('toggle')"
      />
      <RouterLink
        v-if="showTopic"
        :to="{ name: 'topic', params: { topic: message.topic } }"
        class="shrink-0"
        :title="topics.label(message.topic)"
      >
        <TopicIcon :topic="topics.byName(message.topic)" :name="message.topic" />
      </RouterLink>
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-baseline gap-x-2">
          <h3 class="font-semibold" :class="!message.title && 'sr-only'">
            <span v-if="tags.emojis" class="mr-1">{{ tags.emojis }}</span>{{ message.title ?? topics.label(message.topic) }}
          </h3>
          <RouterLink
            v-if="showTopic"
            :to="{ name: 'topic', params: { topic: message.topic } }"
            class="text-xs text-stone-500 hover:underline"
          >
            {{ topics.label(message.topic) }}
          </RouterLink>
        </div>
        <div class="flex items-center gap-2 text-xs text-stone-500">
          <time :datetime="message.createdAt" :title="fullTime(message.createdAt)">{{ shortTime(message.createdAt) }}</time>
          <span v-if="message.priority !== 3" class="rounded px-1.5 py-px font-medium" :class="priorityClass[message.priority]">
            {{ priorityLabels[message.priority] }}
          </span>
          <span v-if="message.source !== 'api'">· {{ message.source }}</span>
        </div>
      </div>
      <div v-if="!selecting" class="-mt-1 -mr-1 flex shrink-0">
        <button v-if="unread" class="btn-icon" title="Als gelesen markieren" :disabled="!!busy" @click="run('read', () => store.markRead(message))">
          <LoaderCircle v-if="busy === 'read'" class="size-4 animate-spin" />
          <Check v-else class="size-4" />
        </button>
        <button class="btn-icon hover:!text-red-600" title="Löschen" :disabled="!!busy" @click="remove">
          <LoaderCircle v-if="busy === 'delete'" class="size-4 animate-spin" />
          <Trash2 v-else class="size-4" />
        </button>
      </div>
    </header>

    <div v-if="message.message" class="text-sm leading-relaxed text-stone-800 dark:text-stone-200">
      <span v-if="!message.title && tags.emojis" class="mr-1">{{ tags.emojis }}</span>
      <MessageText :text="message.message" class="inline" />
    </div>

    <div v-if="images.length" class="mt-3 grid gap-2" :class="images.length === 1 ? 'grid-cols-1' : 'grid-cols-2 sm:grid-cols-3'">
      <button
        v-for="image in images"
        :key="image.id"
        class="overflow-hidden rounded-lg bg-stone-100 dark:bg-stone-800"
        :class="images.length === 1 ? 'w-fit max-w-full' : 'aspect-square'"
        :title="image.fileName"
        @click="emit('openImage', image)"
      >
        <img
          :src="image.thumbnailUrl!"
          :alt="image.fileName"
          loading="lazy"
          class="object-cover transition hover:scale-[1.02]"
          :class="images.length === 1 ? 'max-h-72 w-auto' : 'size-full'"
          :width="image.width ?? undefined"
          :height="image.height ?? undefined"
        />
      </button>
    </div>

    <ul v-if="files.length" class="mt-3 space-y-1">
      <li v-for="file in files" :key="file.id">
        <a :href="file.url" target="_blank" rel="noopener" class="inline-flex items-center gap-2 text-sm text-amber-700 hover:underline dark:text-amber-400">
          <FileText class="size-4" /> {{ file.fileName }} <span class="text-xs text-stone-500">{{ fileSize(file.size) }}</span>
        </a>
      </li>
    </ul>

    <div v-if="tags.rest.length || click" class="mt-3 flex flex-wrap items-center gap-1.5">
      <RouterLink
        v-for="tag in tags.rest"
        :key="tag"
        :to="{ query: { tags: tag } }"
        class="chip hover:bg-stone-200 dark:hover:bg-stone-700"
      >
        #{{ tag }}
      </RouterLink>
      <a v-if="click" :href="click" target="_blank" rel="noopener noreferrer" class="chip text-amber-700 dark:text-amber-400">
        <ExternalLink class="size-3" /> Öffnen
      </a>
    </div>

    <div v-if="message.actions.length" class="mt-3 flex flex-wrap gap-2">
      <div v-for="action in message.actions" :key="action.index" class="flex items-center gap-2">
        <button class="btn" :disabled="!!busy" :aria-label="action.label" :title="`${action.method} ${action.url}`" @click="run(`action-${action.index}`, () => store.runAction(message, action.index))">
          <LoaderCircle v-if="busy === `action-${action.index}`" class="size-4 animate-spin" />
          {{ action.label }}
        </button>
        <span
          v-if="actionState(action)"
          class="text-xs"
          :class="actionState(action)!.ok ? 'text-emerald-700 dark:text-emerald-400' : 'text-red-600 dark:text-red-400'"
        >
          {{ actionState(action)!.text }}
        </span>
      </div>
    </div>

    <p v-if="error" class="mt-2 text-sm text-red-600" role="alert">{{ error }}</p>
  </article>
</template>
