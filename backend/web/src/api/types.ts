// Shapes of the API under /api/v1, as in docs/SPEC.md. Times are UTC in ISO 8601.

export interface Attachment {
  id: string
  fileName: string
  contentType: string
  size: number
  width: number | null
  height: number | null
  url: string
  thumbnailUrl: string | null
}

export interface MessageAction {
  index: number
  label: string
  method: string
  url: string
  clear: boolean
  /** 0: the target did not answer. */
  lastStatus: number | null
  lastRunAt: string | null
}

export interface Message {
  id: number
  topic: string
  title: string | null
  message: string
  priority: number
  tags: string[]
  click: string | null
  source: string
  createdAt: string
  readAt: string | null
  attachments: Attachment[]
  actions: MessageAction[]
}

export interface MessagePage {
  messages: Message[]
  hasMore: boolean
}

export interface Topic {
  name: string
  displayName: string | null
  icon: string | null
  color: string | null
  muted: boolean
  retentionDays: number | null
  total: number
  unread: number
  lastMessage: { id: number; title: string | null; message: string; priority: number; createdAt: string } | null
}

export type TopicPatch = Partial<Pick<Topic, 'displayName' | 'icon' | 'color' | 'muted' | 'retentionDays'>>

export interface ActionResult {
  index: number
  status: number
  ok: boolean
  runAt: string
  markedRead: boolean
}

export interface PublishToken {
  id: string
  name: string
  topics: string[]
  createdAt: string
  lastUsedAt: string | null
}

export interface CreatedPublishToken extends Omit<PublishToken, 'lastUsedAt'> {
  /** Shown only this once. */
  token: string
}

export interface Device {
  id: string
  name: string
  environment: 'sandbox' | 'production'
  topics: string[]
  createdAt: string
  lastSeenAt: string
}

export interface WebUser {
  sub: string
  name: string
}

/** GET /api/v1/config: how this server signs in. */
export interface ClientConfig {
  name: string
  issuer: string | null
  /** `oidc` with an identity server, `key` with the access key of a server without one. */
  login: 'oidc' | 'key'
}

/** Events of GET /api/v1/stream. */
export interface ReadEvent {
  ids: number[]
  topic: string | null
  upToId: number | null
  readAt: string
}

export interface DeletedEvent {
  id: number
  topic: string
}

/** Filters of GET /api/v1/messages; all optional. */
export interface MessageFilter {
  topic?: string
  q?: string
  unread?: boolean
  hasImage?: boolean
  minPriority?: number
  tags?: string[]
  /** Days as YYYY-MM-DD, in local time; until includes the day. */
  since?: string
  until?: string
}
