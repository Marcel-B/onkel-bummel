<script setup lang="ts">
import { BellOff, CheckCheck, Images, LoaderCircle, RefreshCw } from 'lucide-vue-next'
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { errorText } from '@/api/client'
import type { Attachment, Message, MessageFilter } from '@/api/types'
import FilterBar from '@/components/FilterBar.vue'
import ImageLightbox, { type LightboxItem } from '@/components/ImageLightbox.vue'
import MessageCard from '@/components/MessageCard.vue'
import TopicIcon from '@/components/TopicIcon.vue'
import UnreadBadge from '@/components/UnreadBadge.vue'
import { useInfiniteScroll } from '@/composables/useInfiniteScroll'
import { filterFromQuery, filterToQuery, isFiltered } from '@/lib/filter'
import { dayLabel } from '@/lib/format'
import { useMessagesStore } from '@/stores/messages'
import { useTopicsStore } from '@/stores/topics'

const props = defineProps<{ topic?: string }>()

const route = useRoute()
const router = useRouter()
const store = useMessagesStore()
const topics = useTopicsStore()

const filter = computed<MessageFilter>(() => filterFromQuery(route.query, props.topic))
const topic = computed(() => (props.topic ? topics.byName(props.topic) : undefined))
const title = computed(() => (props.topic ? topics.label(props.topic) : 'Alle Nachrichten'))
const unread = computed(() => (props.topic ? (topic.value?.unread ?? 0) : topics.unreadTotal))

watch(filter, (next) => void store.load(next), { immediate: true })

function changeFilter(next: MessageFilter) {
  void router.replace({ query: filterToQuery(next) })
}

/** The messages with a heading for each new day. */
const groups = computed(() => {
  const result: { day: string; messages: Message[] }[] = []
  for (const message of store.messages) {
    const day = dayLabel(message.createdAt)
    const group = result.at(-1)
    if (group?.day === day) group.messages.push(message)
    else result.push({ day, messages: [message] })
  }
  return result
})

const sentinel = ref<HTMLElement | null>(null)
useInfiniteScroll(sentinel, () => store.loadMore())

const markingAll = ref(false)
const actionError = ref('')
async function markAllRead() {
  markingAll.value = true
  actionError.value = ''
  try {
    await store.markAllRead()
  } catch (e) {
    actionError.value = errorText(e)
  } finally {
    markingAll.value = false
  }
}

// The lightbox steps through all images of the messages on screen.
const lightboxItems = computed<LightboxItem[]>(() =>
  store.messages.flatMap((message) => message.attachments.filter((a) => a.thumbnailUrl).map((attachment) => ({ attachment, message }))),
)
const lightboxIndex = ref<number | null>(null)
function openImage(attachment: Attachment) {
  lightboxIndex.value = lightboxItems.value.findIndex((i) => i.attachment.id === attachment.id)
}
</script>

<template>
  <div class="mx-auto max-w-3xl px-3 py-4 sm:px-6">
    <div class="mb-3 flex flex-wrap items-center gap-3">
      <TopicIcon v-if="props.topic" :topic="topic" :name="props.topic" />
      <h1 class="text-xl font-semibold">{{ title }}</h1>
      <BellOff v-if="topic?.muted" class="size-4 text-stone-400" aria-label="stumm" />
      <UnreadBadge :count="unread" />
      <div class="ml-auto flex gap-2">
        <RouterLink v-if="props.topic" :to="{ name: 'gallery', params: { topic: props.topic } }" class="btn">
          <Images class="size-4" /> Galerie
        </RouterLink>
        <button class="btn" :disabled="unread === 0 || markingAll" @click="markAllRead">
          <LoaderCircle v-if="markingAll" class="size-4 animate-spin" />
          <CheckCheck v-else class="size-4" />
          Alle gelesen
        </button>
      </div>
    </div>

    <FilterBar :filter="filter" class="mb-4" @change="changeFilter" />
    <p v-if="filter.q" class="mb-3 text-sm text-stone-500">Suche nach „{{ filter.q }}“</p>
    <p v-if="actionError" class="mb-3 text-sm text-red-600" role="alert">{{ actionError }}</p>

    <button v-if="store.pendingNew > 0" class="btn mb-3 w-full justify-center" @click="store.reload()">
      <RefreshCw class="size-4" />
      {{ store.pendingNew === 1 ? 'Eine neue Nachricht' : `${store.pendingNew} neue Nachrichten` }} – Suche aktualisieren
    </button>

    <p v-if="store.error" class="card p-4 text-sm text-red-600" role="alert">{{ store.error }}</p>
    <div v-else-if="!store.loading && store.messages.length === 0" class="card p-8 text-center text-sm text-stone-500">
      <template v-if="isFiltered(filter)">Keine Nachricht passt zu diesem Filter.</template>
      <template v-else-if="props.topic">In diesem Topic gibt es keine Nachrichten.</template>
      <template v-else>Noch keine Nachrichten. Sobald ein Sender etwas schickt, erscheint es hier sofort.</template>
    </div>

    <section v-for="group in groups" :key="group.day" class="mb-4">
      <h2 class="sticky top-0 z-[1] -mx-1 mb-2 bg-stone-50/90 px-1 py-1 text-xs font-semibold tracking-wide text-stone-500 uppercase backdrop-blur dark:bg-stone-950/90">
        {{ group.day }}
      </h2>
      <div class="space-y-2">
        <MessageCard v-for="message in group.messages" :key="message.id" :message="message" :show-topic="!props.topic" @open-image="openImage" />
      </div>
    </section>

    <div ref="sentinel" class="flex h-12 items-center justify-center text-stone-400">
      <LoaderCircle v-if="store.loading" class="size-5 animate-spin" />
      <span v-else-if="store.messages.length > 0 && !store.hasMore" class="text-xs">Das war alles.</span>
    </div>

    <ImageLightbox
      v-if="lightboxIndex !== null"
      :items="lightboxItems"
      :index="lightboxIndex"
      @move="lightboxIndex = $event"
      @close="lightboxIndex = null"
    />
  </div>
</template>
