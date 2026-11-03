<script setup lang="ts">
import { computed, h, type VNode } from 'vue'
import { parse, type Piece } from '@/lib/markdown'

const props = defineProps<{ text: string }>()

function render(pieces: Piece[]): (VNode | string)[] {
  return pieces.map((piece) => {
    switch (piece.kind) {
      case 'text':
        return piece.text
      case 'break':
        return h('br')
      case 'code':
        return h('code', { class: 'rounded bg-stone-100 px-1 py-0.5 text-[0.9em] dark:bg-stone-800' }, piece.text)
      case 'bold':
        return h('strong', render(piece.children))
      case 'italic':
        return h('em', render(piece.children))
      case 'link':
        return h(
          'a',
          { href: piece.href, target: '_blank', rel: 'noopener noreferrer', class: 'text-amber-700 underline underline-offset-2 dark:text-amber-400' },
          render(piece.children),
        )
    }
  })
}

const content = computed(() => render(parse(props.text)))
</script>

<template>
  <p class="break-words whitespace-normal"><component :is="() => content" /></p>
</template>
