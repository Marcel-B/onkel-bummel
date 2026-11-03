<script setup lang="ts">
import { BellOff, Images, Inbox } from 'lucide-vue-next'
import { RouterLink } from 'vue-router'
import { useTopicsStore } from '@/stores/topics'
import TopicIcon from './TopicIcon.vue'
import UnreadBadge from './UnreadBadge.vue'

const topics = useTopicsStore()

const linkClass =
  'flex items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm text-stone-700 hover:bg-stone-200/70 dark:text-stone-300 dark:hover:bg-stone-800'
const activeClass = '!bg-amber-100 !text-amber-900 dark:!bg-amber-500/15 dark:!text-amber-200'
</script>

<template>
  <nav aria-label="Topics" class="space-y-0.5">
    <RouterLink to="/" :class="linkClass" :exact-active-class="activeClass">
      <span class="flex size-7 items-center justify-center rounded-md bg-stone-200 dark:bg-stone-800"><Inbox class="size-4" /></span>
      <span class="flex-1 font-medium">Alle</span>
      <UnreadBadge :count="topics.unreadTotal" />
    </RouterLink>
    <RouterLink to="/galerie" :class="linkClass" :exact-active-class="activeClass">
      <span class="flex size-7 items-center justify-center rounded-md bg-stone-200 dark:bg-stone-800"><Images class="size-4" /></span>
      <span class="flex-1 font-medium">Galerie</span>
    </RouterLink>

    <h2 class="px-2.5 pt-4 pb-1 text-xs font-semibold tracking-wide text-stone-500 uppercase">Topics</h2>
    <p v-if="topics.error" class="px-2.5 text-sm text-red-600">{{ topics.error }}</p>
    <p v-else-if="topics.loaded && topics.topics.length === 0" class="px-2.5 text-sm text-stone-500">
      Noch keine Nachrichten. Ein Topic entsteht mit seiner ersten Nachricht.
    </p>
    <RouterLink
      v-for="topic in topics.topics"
      :key="topic.name"
      :to="{ name: 'topic', params: { topic: topic.name } }"
      :class="linkClass"
      :active-class="activeClass"
    >
      <TopicIcon :topic="topic" />
      <span class="min-w-0 flex-1">
        <span class="block truncate" :class="topic.unread > 0 && 'font-semibold text-stone-900 dark:text-white'">
          {{ topic.displayName ?? topic.name }}
        </span>
        <span v-if="topic.lastMessage" class="block truncate text-xs text-stone-500">
          {{ topic.lastMessage.title ?? topic.lastMessage.message }}
        </span>
      </span>
      <BellOff v-if="topic.muted" class="size-3.5 text-stone-400" aria-label="stumm" />
      <UnreadBadge :count="topic.unread" />
    </RouterLink>
  </nav>
</template>
