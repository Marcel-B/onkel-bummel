import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, errorText } from '@/api/client'
import type { DeletedEvent, Message, MessageFilter, ReadEvent } from '@/api/types'
import { matchesLive } from '@/lib/filter'
import { useTopicsStore } from './topics'

const pageSize = 50
/** At most this many ids per request to POST messages/delete. */
const deleteBatch = 500

/** The list on screen (timeline or gallery): one filter at a time, newest first, older pages on demand. */
export const useMessagesStore = defineStore('messages', () => {
  const filter = ref<MessageFilter>({})
  const messages = ref<Message[]>([])
  const hasMore = ref(false)
  const loading = ref(false)
  const error = ref('')
  /** New messages for a search, which only the server can match; the list offers to reload. */
  const pendingNew = ref(0)

  // A newer load wins over an older one still on its way.
  let generation = 0

  async function load(next: MessageFilter) {
    filter.value = next
    const current = ++generation
    loading.value = true
    error.value = ''
    pendingNew.value = 0
    try {
      const page = await api.messages(next, { limit: pageSize })
      if (current !== generation) return
      messages.value = page.messages
      hasMore.value = page.hasMore
    } catch (e) {
      if (current !== generation) return
      messages.value = []
      hasMore.value = false
      error.value = errorText(e)
    } finally {
      if (current === generation) loading.value = false
    }
  }

  async function loadMore() {
    const last = messages.value.at(-1)
    if (!hasMore.value || loading.value || !last) return
    const current = generation
    loading.value = true
    try {
      const page = await api.messages(filter.value, { before: last.id, limit: pageSize })
      if (current !== generation) return
      messages.value.push(...page.messages)
      hasMore.value = page.hasMore
    } catch (e) {
      if (current === generation) error.value = errorText(e)
    } finally {
      if (current === generation) loading.value = false
    }
  }

  /** After the live connection was gone: whatever came meanwhile, and the read states, come from the server. */
  function reload() {
    return load(filter.value)
  }

  function onMessage(message: Message) {
    if (messages.value.some((m) => m.id === message.id)) return
    const matches = matchesLive(message, filter.value)
    if (matches === null) pendingNew.value++
    else if (matches) messages.value.unshift(message)
  }

  function onRead(event: ReadEvent) {
    for (const message of messages.value) {
      if (message.readAt) continue
      const hit =
        event.ids.length > 0
          ? event.ids.includes(message.id)
          : message.id <= (event.upToId ?? Infinity) && (event.topic === null || event.topic === message.topic)
      if (hit) message.readAt = event.readAt
    }
    // A list of unread messages keeps them until it is loaded again, so nothing jumps away while reading.
  }

  function onDeleted(event: DeletedEvent) {
    messages.value = messages.value.filter((m) => m.id !== event.id)
  }

  async function markRead(message: Message) {
    await api.markRead(message.id)
    message.readAt ??= new Date().toISOString()
    useTopicsStore().reloadSoon()
  }

  /** Everything in the list's topic (or all topics) up to the newest message shown. */
  async function markAllRead() {
    const newest = messages.value[0]
    const count = await api.markAllRead(filter.value.topic ?? null, newest?.id ?? null)
    const now = new Date().toISOString()
    for (const message of messages.value) message.readAt ??= now
    useTopicsStore().reloadSoon()
    return count
  }

  async function remove(message: Message) {
    await api.deleteMessage(message.id)
    onDeleted({ id: message.id, topic: message.topic })
    useTopicsStore().reloadSoon()
  }

  /** Deletes the chosen messages, in batches the server accepts. */
  async function removeMany(ids: number[]) {
    for (let start = 0; start < ids.length; start += deleteBatch) {
      const batch = ids.slice(start, start + deleteBatch)
      await api.deleteMessages(batch)
      messages.value = messages.value.filter((m) => !batch.includes(m.id))
    }
    useTopicsStore().reloadSoon()
  }

  async function runAction(message: Message, index: number) {
    const result = await api.runAction(message.id, index)
    const action = message.actions.find((a) => a.index === index)
    if (action) {
      action.lastStatus = result.status
      action.lastRunAt = result.runAt
    }
    if (result.markedRead) {
      message.readAt ??= result.runAt
      useTopicsStore().reloadSoon()
    }
    return result
  }

  return {
    filter, messages, hasMore, loading, error, pendingNew,
    load, loadMore, reload, onMessage, onRead, onDeleted, markRead, markAllRead, remove, removeMany, runAction,
  }
})
