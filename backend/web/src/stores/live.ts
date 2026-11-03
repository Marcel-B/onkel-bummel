import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { DeletedEvent, Message, ReadEvent } from '@/api/types'
import { useMessagesStore } from './messages'
import { useTopicsStore } from './topics'

/**
 * The live connection (server-sent events) for all topics. EventSource reconnects by itself; after each
 * reconnect the list and the counters are loaded again, since events in between are lost.
 */
export const useLiveStore = defineStore('live', () => {
  const connected = ref(false)
  let source: EventSource | null = null
  let wasConnected = false

  function start() {
    if (source) return
    source = new EventSource('/api/v1/stream')
    const messages = useMessagesStore()
    const topics = useTopicsStore()

    source.addEventListener('open', () => {
      connected.value = true
      if (wasConnected) {
        void messages.reload()
        void topics.load()
      }
      wasConnected = true
    })
    source.addEventListener('error', () => {
      connected.value = false
    })
    source.addEventListener('message', (event) => {
      messages.onMessage(JSON.parse(event.data) as Message)
      topics.reloadSoon()
    })
    source.addEventListener('read', (event) => {
      messages.onRead(JSON.parse(event.data) as ReadEvent)
      topics.reloadSoon()
    })
    source.addEventListener('deleted', (event) => {
      messages.onDeleted(JSON.parse(event.data) as DeletedEvent)
      topics.reloadSoon()
    })
  }

  function stop() {
    source?.close()
    source = null
    connected.value = false
  }

  return { connected, start, stop }
})
