<script setup lang="ts">
defineProps<{ count: number }>()
</script>

<template>
  <span
    v-if="count > 0"
    class="min-w-5 rounded-full bg-amber-600 px-1.5 py-0.5 text-center text-xs leading-none font-semibold text-white tabular-nums"
    :aria-label="`${count} ungelesen`"
  >
    {{ count > 999 ? '999+' : count }}
  </span>
</template>
