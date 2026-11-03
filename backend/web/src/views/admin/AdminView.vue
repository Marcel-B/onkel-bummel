<script setup lang="ts">
import { RouterLink, RouterView } from 'vue-router'

const tabs = [
  { name: 'admin-topics', label: 'Topics' },
  { name: 'admin-tokens', label: 'Publish-Tokens' },
  { name: 'admin-devices', label: 'Geräte' },
  { name: 'admin-app', label: 'App einrichten' },
]
</script>

<template>
  <div class="mx-auto max-w-4xl px-3 py-4 sm:px-6">
    <h1 class="mb-3 text-xl font-semibold">Verwaltung</h1>
    <nav class="mb-5 flex gap-1 overflow-x-auto border-b border-stone-200 dark:border-stone-800" aria-label="Verwaltung">
      <RouterLink
        v-for="tab in tabs"
        :key="tab.name"
        :to="{ name: tab.name }"
        class="-mb-px border-b-2 border-transparent px-3 py-2 text-sm whitespace-nowrap text-stone-600 hover:text-stone-900 dark:text-stone-400 dark:hover:text-white"
        active-class="!border-amber-600 !text-amber-800 dark:!text-amber-300"
      >
        {{ tab.label }}
      </RouterLink>
    </nav>
    <RouterView />
  </div>
</template>
