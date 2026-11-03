<script setup lang="ts">
import { List, LoaderCircle } from 'lucide-vue-next'
import { computed, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import type { MessageFilter } from '@/api/types'
import FilterBar from '@/components/FilterBar.vue'
import ImageLightbox, { type LightboxItem } from '@/components/ImageLightbox.vue'
import TopicIcon from '@/components/TopicIcon.vue'
import { useInfiniteScroll } from '@/composables/useInfiniteScroll'
import { filterFromQuery, filterToQuery } from '@/lib/filter'
import { shortTime } from '@/lib/format'
import { useMessagesStore } from '@/stores/messages'
import { useTopicsStore } from '@/stores/topics'

const props = defineProps<{ topic?: string }>()

const route = useRoute()
const router = useRouter()
const store = useMessagesStore()
const topics = useTopicsStore()

const filter = computed<MessageFilter>(() => ({ ...filterFromQuery(route.query, props.topic), hasImage: true }))
watch(filter, (next) => void store.load(next), { immediate: true })

function changeFilter(next: MessageFilter) {
  void router.replace({ query: filterToQuery({ ...next, hasImage: undefined }) })
}

const items = computed<LightboxItem[]>(() =>
  store.messages.flatMap((message) => message.attachments.filter((a) => a.thumbnailUrl).map((attachment) => ({ attachment, message }))),
)
const open = ref<number | null>(null)

const sentinel = ref<HTMLElement | null>(null)
useInfiniteScroll(sentinel, () => store.loadMore())
</script>

<template>
  <div class="mx-auto max-w-6xl px-3 py-4 sm:px-6">
    <div class="mb-3 flex flex-wrap items-center gap-3">
      <TopicIcon v-if="props.topic" :topic="topics.byName(props.topic)" :name="props.topic" />
      <h1 class="text-xl font-semibold">Galerie{{ props.topic ? ` · ${topics.label(props.topic)}` : '' }}</h1>
      <RouterLink v-if="props.topic" :to="{ name: 'topic', params: { topic: props.topic } }" class="btn ml-auto">
        <List class="size-4" /> Verlauf
      </RouterLink>
    </div>
    <FilterBar :filter="filter" hide-images class="mb-4" @change="changeFilter" />

    <p v-if="store.error" class="card p-4 text-sm text-red-600" role="alert">{{ store.error }}</p>
    <div v-else-if="!store.loading && items.length === 0" class="card p-8 text-center text-sm text-stone-500">Keine Bilder.</div>

    <ul class="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-5">
      <li v-for="(item, index) in items" :key="item.attachment.id">
        <button class="group relative block aspect-square w-full overflow-hidden rounded-lg bg-stone-200 dark:bg-stone-800" @click="open = index">
          <img :src="item.attachment.thumbnailUrl!" :alt="item.attachment.fileName" loading="lazy" class="size-full object-cover transition group-hover:scale-105" />
          <span class="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/70 to-transparent p-2 text-left text-xs text-white opacity-0 transition group-hover:opacity-100">
            <span class="block truncate">{{ item.message.title ?? item.message.message }}</span>
            <span class="text-white/70">{{ shortTime(item.message.createdAt) }}<template v-if="!props.topic"> · {{ topics.label(item.message.topic) }}</template></span>
          </span>
        </button>
      </li>
    </ul>

    <div ref="sentinel" class="flex h-12 items-center justify-center text-stone-400">
      <LoaderCircle v-if="store.loading" class="size-5 animate-spin" />
    </div>

    <ImageLightbox v-if="open !== null" :items="items" :index="open" @move="open = $event" @close="open = null" />
  </div>
</template>
