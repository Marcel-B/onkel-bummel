import { createPinia, setActivePinia } from 'pinia'
import type { Message } from '@/api/types'
import { useMessagesStore } from './messages'

function message(id: number, changes: Partial<Message> = {}): Message {
  return {
    id, topic: 'garage', title: null, message: `Nachricht ${id}`, priority: 3, tags: [], click: null, source: 'api',
    createdAt: new Date().toISOString(), readAt: null, attachments: [], actions: [], ...changes,
  }
}

describe('live events in the list', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('puts new messages of the topic on top, once', () => {
    const store = useMessagesStore()
    store.filter = { topic: 'garage' }
    store.messages = [message(1)]

    store.onMessage(message(2))
    store.onMessage(message(2))
    store.onMessage(message(3, { topic: 'kamera' }))

    expect(store.messages.map((m) => m.id)).toEqual([2, 1])
  })

  it('counts new messages for a search instead of guessing', () => {
    const store = useMessagesStore()
    store.filter = { q: 'tor' }

    store.onMessage(message(5))

    expect(store.messages).toEqual([])
    expect(store.pendingNew).toBe(1)
  })

  it('marks single messages, a topic up to an id, or everything as read', () => {
    const store = useMessagesStore()
    store.messages = [message(4, { topic: 'kamera' }), message(3), message(2), message(1)]

    store.onRead({ ids: [1], topic: 'garage', upToId: null, readAt: 'a' })
    store.onRead({ ids: [], topic: 'garage', upToId: 2, readAt: 'b' })
    expect(store.messages.map((m) => m.readAt)).toEqual([null, null, 'b', 'a'])

    store.onRead({ ids: [], topic: null, upToId: null, readAt: 'c' })
    expect(store.messages.map((m) => m.readAt)).toEqual(['c', 'c', 'b', 'a'])
  })

  it('drops deleted messages', () => {
    const store = useMessagesStore()
    store.messages = [message(2), message(1)]

    store.onDeleted({ id: 2, topic: 'garage' })

    expect(store.messages.map((m) => m.id)).toEqual([1])
  })
})
