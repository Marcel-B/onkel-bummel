<script setup lang="ts">
import { LoaderCircle, Smartphone, Trash2 } from 'lucide-vue-next'
import { computed, onMounted, reactive, ref } from 'vue'
import { api, errorText } from '@/api/client'
import type { Device } from '@/api/types'
import { shortTime } from '@/lib/format'
import { useTopicsStore } from '@/stores/topics'

const topics = useTopicsStore()
const devices = ref<Device[]>([])
const loading = ref(true)
const error = ref('')
const saving = reactive<Record<string, boolean>>({})

const topicNames = computed(() => topics.topics.map((t) => t.name).sort())

onMounted(async () => {
  try {
    devices.value = await api.devices()
  } catch (e) {
    error.value = errorText(e)
  } finally {
    loading.value = false
  }
})

/** Topics the device follows; with "*" all of them, also those that do not exist yet. */
function follows(device: Device, topic: string) {
  return device.topics.includes('*') || device.topics.includes(topic)
}

async function setTopics(device: Device, next: string[]) {
  saving[device.id] = true
  error.value = ''
  try {
    Object.assign(device, await api.setSubscriptions(device.id, next))
  } catch (e) {
    error.value = errorText(e)
  } finally {
    saving[device.id] = false
  }
}

function toggleAll(device: Device, all: boolean) {
  // Leaving "all" keeps today's topics, so nothing goes quiet by surprise.
  void setTopics(device, all ? ['*'] : topicNames.value)
}

function toggle(device: Device, topic: string, on: boolean) {
  const current = device.topics.filter((t) => t !== '*')
  void setTopics(device, on ? [...current, topic] : current.filter((t) => t !== topic))
}

async function remove(device: Device) {
  if (!confirm(`„${device.name}“ abmelden? Es bekommt keine Pushes mehr, bis sich die App neu anmeldet.`)) return
  try {
    await api.deleteDevice(device.id)
    devices.value = devices.value.filter((d) => d.id !== device.id)
  } catch (e) {
    error.value = errorText(e)
  }
}
</script>

<template>
  <p class="mb-4 text-sm text-stone-600 dark:text-stone-400">
    Deine iPhones mit der App. Jedes bekommt Pushes für die abonnierten Topics, außer für stumme. Die Abos lassen sich
    auch in der App unter Einstellungen ändern.
  </p>
  <p v-if="error" class="mb-3 text-sm text-red-600" role="alert">{{ error }}</p>
  <LoaderCircle v-if="loading" class="mx-auto size-5 animate-spin text-stone-400" />
  <p v-else-if="devices.length === 0" class="card p-6 text-center text-sm text-stone-500">
    Noch kein Gerät. Die App meldet das iPhone bei der Einrichtung an.
  </p>

  <div class="space-y-3">
    <section v-for="device in devices" :key="device.id" class="card p-4">
      <header class="mb-3 flex items-center gap-3">
        <Smartphone class="size-5 text-stone-500" />
        <div class="min-w-0 flex-1">
          <p class="font-medium">{{ device.name }}</p>
          <p class="text-xs text-stone-500">
            {{ device.environment === 'sandbox' ? 'Entwicklungs-Build' : 'TestFlight / App Store' }} ·
            zuletzt gesehen {{ shortTime(device.lastSeenAt) }}
          </p>
        </div>
        <LoaderCircle v-if="saving[device.id]" class="size-4 animate-spin text-stone-400" />
        <button class="btn-icon hover:!text-red-600" :title="`„${device.name}“ abmelden`" @click="remove(device)">
          <Trash2 class="size-4" />
        </button>
      </header>
      <fieldset :disabled="saving[device.id]" class="flex flex-wrap gap-x-4 gap-y-2 text-sm">
        <legend class="sr-only">Abos von {{ device.name }}</legend>
        <label class="inline-flex items-center gap-1.5 font-medium">
          <input type="checkbox" class="accent-amber-600" :checked="device.topics.includes('*')" @change="toggleAll(device, ($event.target as HTMLInputElement).checked)" />
          alle Topics
        </label>
        <label v-for="topic in topicNames" :key="topic" class="inline-flex items-center gap-1.5" :class="device.topics.includes('*') && 'opacity-50'">
          <input
            type="checkbox"
            class="accent-amber-600"
            :checked="follows(device, topic)"
            :disabled="device.topics.includes('*')"
            @change="toggle(device, topic, ($event.target as HTMLInputElement).checked)"
          />
          {{ topics.label(topic) }}
        </label>
      </fieldset>
    </section>
  </div>
</template>
