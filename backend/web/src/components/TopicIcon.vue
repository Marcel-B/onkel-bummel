<script setup lang="ts">
import { computed } from 'vue'
import type { Topic } from '@/api/types'

const props = defineProps<{ topic: Pick<Topic, 'name' | 'icon' | 'color'> | undefined; name?: string }>()

// Without an icon of its own, a topic shows its first letter.
const text = computed(() => props.topic?.icon ?? (props.topic?.name ?? props.name ?? '?').charAt(0).toUpperCase())
const color = computed(() => props.topic?.color ?? null)
</script>

<template>
  <span
    class="flex size-7 shrink-0 items-center justify-center rounded-md text-sm font-semibold"
    :class="color ? 'text-white' : 'bg-stone-200 text-stone-600 dark:bg-stone-800 dark:text-stone-300'"
    :style="color ? { backgroundColor: color } : undefined"
    aria-hidden="true"
  >
    {{ text }}
  </span>
</template>
