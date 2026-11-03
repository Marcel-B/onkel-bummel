import type { Message, MessageFilter } from '@/api/types'
import type { LocationQuery } from 'vue-router'

/**
 * Does a message that just came in live belong in a list with this filter? Null when only the server can tell,
 * i.e. for a full-text search.
 */
export function matchesLive(message: Message, filter: MessageFilter): boolean | null {
  if (filter.q?.trim()) return null
  if (filter.topic && message.topic !== filter.topic) return false
  if (filter.unread && message.readAt) return false
  if (filter.hasImage && !message.attachments.some((a) => a.contentType.startsWith('image/'))) return false
  if (filter.minPriority && message.priority < filter.minPriority) return false
  if (filter.tags?.some((tag) => !message.tags.includes(tag))) return false
  const created = new Date(message.createdAt)
  if (filter.since && created < new Date(`${filter.since}T00:00:00`)) return false
  if (filter.until) {
    const end = new Date(`${filter.until}T00:00:00`)
    end.setDate(end.getDate() + 1)
    if (created >= end) return false
  }
  return true
}

/** The filter of a list as it stands in the address, so it survives a reload and can be bookmarked. */
export function filterFromQuery(query: LocationQuery, topic?: string): MessageFilter {
  const text = (key: string) => {
    const value = query[key]
    return typeof value === 'string' && value !== '' ? value : undefined
  }
  const priority = Number(text('prio'))
  return {
    topic,
    q: text('q'),
    unread: text('ungelesen') === '1',
    hasImage: text('bilder') === '1',
    minPriority: priority >= 1 && priority <= 5 ? priority : undefined,
    tags: text('tags')?.split(',').map((t) => t.trim()).filter(Boolean),
    since: text('von'),
    until: text('bis'),
  }
}

export function filterToQuery(filter: MessageFilter): Record<string, string> {
  const query: Record<string, string> = {}
  if (filter.q?.trim()) query.q = filter.q.trim()
  if (filter.unread) query.ungelesen = '1'
  if (filter.hasImage) query.bilder = '1'
  if (filter.minPriority) query.prio = String(filter.minPriority)
  if (filter.tags?.length) query.tags = filter.tags.join(',')
  if (filter.since) query.von = filter.since
  if (filter.until) query.bis = filter.until
  return query
}

/** Is anything filtered besides the topic? */
export function isFiltered(filter: MessageFilter) {
  return Object.keys(filterToQuery(filter)).length > 0
}
