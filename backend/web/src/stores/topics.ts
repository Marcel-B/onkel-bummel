import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api, errorText } from '@/api/client'
import type { Topic, TopicPatch } from '@/api/types'

export const useTopicsStore = defineStore('topics', () => {
  const topics = ref<Topic[]>([])
  const loaded = ref(false)
  const error = ref('')

  const unreadTotal = computed(() => topics.value.reduce((sum, t) => sum + t.unread, 0))

  function byName(name: string) {
    return topics.value.find((t) => t.name === name)
  }

  function label(name: string) {
    return byName(name)?.displayName ?? name
  }

  async function load() {
    try {
      topics.value = await api.topics()
      error.value = ''
    } catch (e) {
      error.value = errorText(e)
    } finally {
      loaded.value = true
    }
  }

  // Live events come in bursts (a topic marked read, a group of alarms); one reload covers them all.
  let reloadTimer: ReturnType<typeof setTimeout> | undefined
  function reloadSoon() {
    clearTimeout(reloadTimer)
    reloadTimer = setTimeout(load, 300)
  }

  async function patch(name: string, changes: TopicPatch) {
    const updated = await api.patchTopic(name, changes)
    const index = topics.value.findIndex((t) => t.name === name)
    if (index >= 0) topics.value[index] = updated
    return updated
  }

  return { topics, loaded, error, unreadTotal, byName, label, load, reloadSoon, patch }
})
