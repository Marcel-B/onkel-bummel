<script setup lang="ts">
import { ChevronLeft, ChevronRight, Download, X } from 'lucide-vue-next'
import { computed, onBeforeUnmount, onMounted } from 'vue'
import type { Attachment, Message } from '@/api/types'
import { fileSize, fullTime } from '@/lib/format'

/** One image of a series, with the message it belongs to. */
export interface LightboxItem {
  attachment: Attachment
  message: Message
}

const props = defineProps<{ items: LightboxItem[]; index: number }>()
const emit = defineEmits<{ close: []; move: [index: number] }>()

const item = computed(() => props.items[props.index])

function move(step: number) {
  const next = props.index + step
  if (next >= 0 && next < props.items.length) emit('move', next)
}

function onKey(event: KeyboardEvent) {
  if (event.key === 'Escape') emit('close')
  else if (event.key === 'ArrowLeft') move(-1)
  else if (event.key === 'ArrowRight') move(1)
}

onMounted(() => {
  document.addEventListener('keydown', onKey)
  document.body.style.overflow = 'hidden'
})
onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKey)
  document.body.style.overflow = ''
})
</script>

<template>
  <div
    v-if="item"
    class="fixed inset-0 z-50 flex flex-col bg-black/90 text-white"
    role="dialog"
    aria-modal="true"
    :aria-label="item.attachment.fileName"
    @click.self="emit('close')"
  >
    <div class="flex items-center gap-3 p-3 text-sm">
      <div class="min-w-0 flex-1">
        <p class="truncate font-medium">{{ item.message.title ?? item.attachment.fileName }}</p>
        <p class="text-xs text-white/60">
          {{ fullTime(item.message.createdAt) }} · {{ item.attachment.fileName }} · {{ fileSize(item.attachment.size) }}
          <template v-if="items.length > 1"> · {{ index + 1 }} / {{ items.length }}</template>
        </p>
      </div>
      <a :href="`${item.attachment.url}?download=true`" class="rounded-lg p-2 hover:bg-white/10" title="Original herunterladen">
        <Download class="size-5" />
      </a>
      <button class="rounded-lg p-2 hover:bg-white/10" title="Schließen (Esc)" @click="emit('close')"><X class="size-5" /></button>
    </div>
    <div class="relative flex min-h-0 flex-1 items-center justify-center px-12 pb-6" @click.self="emit('close')">
      <img :key="item.attachment.id" :src="item.attachment.url" :alt="item.attachment.fileName" class="max-h-full max-w-full object-contain" />
      <button
        v-if="index > 0"
        class="absolute top-1/2 left-2 -translate-y-1/2 rounded-full bg-white/10 p-2 hover:bg-white/20"
        title="Vorheriges (←)"
        @click="move(-1)"
      >
        <ChevronLeft class="size-6" />
      </button>
      <button
        v-if="index < items.length - 1"
        class="absolute top-1/2 right-2 -translate-y-1/2 rounded-full bg-white/10 p-2 hover:bg-white/20"
        title="Nächstes (→)"
        @click="move(1)"
      >
        <ChevronRight class="size-6" />
      </button>
    </div>
  </div>
</template>
