import type { Message } from '@/api/types'
import { filterFromQuery, filterToQuery, matchesLive } from './filter'

function message(changes: Partial<Message> = {}): Message {
  return {
    id: 1, topic: 'garage', title: null, message: 'Tor offen', priority: 3, tags: [], click: null, source: 'api',
    createdAt: new Date().toISOString(), readAt: null, attachments: [], actions: [], ...changes,
  }
}

describe('matchesLive', () => {
  it('goes by topic, priority, tags and images', () => {
    expect(matchesLive(message(), {})).toBe(true)
    expect(matchesLive(message(), { topic: 'kamera' })).toBe(false)
    expect(matchesLive(message({ priority: 4 }), { minPriority: 4 })).toBe(true)
    expect(matchesLive(message({ priority: 3 }), { minPriority: 4 })).toBe(false)
    expect(matchesLive(message({ tags: ['tor', 'warning'] }), { tags: ['warning'] })).toBe(true)
    expect(matchesLive(message({ tags: ['tor'] }), { tags: ['tor', 'warning'] })).toBe(false)
    expect(matchesLive(message(), { hasImage: true })).toBe(false)
  })

  it('leaves a new message out of a list that ends in the past', () => {
    expect(matchesLive(message(), { until: '2020-01-01' })).toBe(false)
    expect(matchesLive(message(), { since: '2020-01-01' })).toBe(true)
  })

  it('cannot tell for a search', () => {
    expect(matchesLive(message(), { q: 'tor' })).toBeNull()
  })
})

describe('filter in the address', () => {
  it('goes there and back', () => {
    const filter = { q: 'tor', unread: true, hasImage: true, minPriority: 4, tags: ['a', 'b'], since: '2026-11-01', until: '2026-11-03' }
    const query = filterToQuery(filter)

    expect(query).toEqual({ q: 'tor', ungelesen: '1', bilder: '1', prio: '4', tags: 'a,b', von: '2026-11-01', bis: '2026-11-03' })
    expect(filterFromQuery(query, 'garage')).toEqual({ topic: 'garage', ...filter })
  })

  it('ignores what makes no sense', () => {
    expect(filterFromQuery({ prio: '9', q: '' })).toMatchObject({ minPriority: undefined, q: undefined })
  })
})
