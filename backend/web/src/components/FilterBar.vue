<script setup lang="ts">
import { X } from 'lucide-vue-next'
import { computed } from 'vue'
import type { MessageFilter } from '@/api/types'
import { isFiltered } from '@/lib/filter'
import { priorityLabels } from '@/lib/format'

const props = defineProps<{ filter: MessageFilter; hideImages?: boolean }>()
const emit = defineEmits<{ change: [filter: MessageFilter] }>()

function set<K extends keyof MessageFilter>(key: K, value: MessageFilter[K]) {
  emit('change', { ...props.filter, [key]: value })
}

const tagsText = computed(() => props.filter.tags?.join(', ') ?? '')

function setTags(text: string) {
  const tags = text.split(',').map((t) => t.trim()).filter(Boolean)
  set('tags', tags.length ? tags : undefined)
}

function clear() {
  emit('change', { topic: props.filter.topic })
}
</script>

<template>
  <div class="flex flex-wrap items-center gap-2 text-sm">
    <label class="inline-flex items-center gap-1.5">
      <input type="checkbox" class="accent-amber-600" :checked="filter.unread" @change="set('unread', ($event.target as HTMLInputElement).checked || undefined)" />
      ungelesen
    </label>
    <label v-if="!hideImages" class="inline-flex items-center gap-1.5">
      <input type="checkbox" class="accent-amber-600" :checked="filter.hasImage" @change="set('hasImage', ($event.target as HTMLInputElement).checked || undefined)" />
      mit Bildern
    </label>
    <select
      class="input py-1"
      aria-label="Mindestens diese Priorität"
      :value="filter.minPriority ?? ''"
      @change="set('minPriority', Number(($event.target as HTMLSelectElement).value) || undefined)"
    >
      <option value="">jede Priorität</option>
      <option v-for="p in [2, 3, 4, 5]" :key="p" :value="p">ab {{ priorityLabels[p] }}</option>
    </select>
    <input
      class="input w-36 py-1"
      placeholder="Tags"
      aria-label="Tags, mit Komma getrennt"
      :value="tagsText"
      @change="setTags(($event.target as HTMLInputElement).value)"
    />
    <label class="inline-flex items-center gap-1">
      von
      <input type="date" class="input py-1" :value="filter.since ?? ''" @change="set('since', ($event.target as HTMLInputElement).value || undefined)" />
    </label>
    <label class="inline-flex items-center gap-1">
      bis
      <input type="date" class="input py-1" :value="filter.until ?? ''" @change="set('until', ($event.target as HTMLInputElement).value || undefined)" />
    </label>
    <button v-if="isFiltered(filter)" class="btn py-1" @click="clear"><X class="size-3.5" /> Filter löschen</button>
  </div>
</template>
