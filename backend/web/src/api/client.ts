import axios, { AxiosError } from 'axios'
import type {
  ActionResult,
  ClientConfig,
  CreatedPublishToken,
  Device,
  Message,
  MessageFilter,
  MessagePage,
  PublishToken,
  Topic,
  TopicPatch,
  WebUser,
} from './types'

// The session cookie comes along by itself (same origin). The server takes changes with the cookie only
// with this header, which another site cannot send without a CORS preflight.
export const http = axios.create({
  baseURL: '/api/v1',
  headers: { 'X-Requested-With': 'XMLHttpRequest' },
})

/**
 * Sends the browser to the identity server and back to where it is now. Without identity server the server sends it
 * straight back, and the web interface asks for the access key.
 */
export function signIn(returnUrl = location.pathname + location.search) {
  location.assign(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`)
}

http.interceptors.response.use(undefined, (error: unknown) => {
  // The session ran out: sign in again, the identity server usually lets the user through without asking.
  if (error instanceof AxiosError && error.response?.status === 401) signIn()
  return Promise.reject(error)
})

/** The detail of a problem answer (RFC 7807), or a general text. */
export function errorText(error: unknown): string {
  if (error instanceof AxiosError) {
    const problem = error.response?.data as { detail?: string; title?: string } | undefined
    if (problem?.detail) return problem.detail
    if (problem?.title) return problem.title
    if (!error.response) return 'Der Server antwortet nicht.'
    return `Fehler ${error.response.status}`
  }
  return String(error)
}

/** Query parameters of GET messages for a filter. */
export function filterParams(filter: MessageFilter): Record<string, string> {
  const params: Record<string, string> = {}
  if (filter.topic) params.topics = filter.topic
  if (filter.q?.trim()) params.q = filter.q.trim()
  if (filter.unread) params.unread = '1'
  if (filter.hasImage) params.has_image = '1'
  if (filter.minPriority) params.min_priority = String(filter.minPriority)
  if (filter.tags?.length) params.tags = filter.tags.join(',')
  if (filter.since) params.since = new Date(`${filter.since}T00:00:00`).toISOString()
  if (filter.until) {
    const end = new Date(`${filter.until}T00:00:00`)
    end.setDate(end.getDate() + 1)
    params.until = end.toISOString()
  }
  return params
}

export const api = {
  async me(): Promise<WebUser> {
    // Outside of /api/v1, and a 401 here is an answer, not a reason to redirect.
    return (await axios.get<WebUser>('/auth/me')).data
  },
  async config(): Promise<ClientConfig> {
    return (await axios.get<ClientConfig>('/api/v1/config')).data
  },
  /** Signs in with the access key; the session cookie comes back. A wrong key is a 401, no reason to redirect. */
  async signInWithKey(key: string) {
    await axios.post('/auth/key', { key })
  },

  async messages(filter: MessageFilter, page: { before?: number; after?: number; limit?: number } = {}) {
    const params = { ...filterParams(filter), ...page }
    return (await http.get<MessagePage>('/messages', { params })).data
  },
  async message(id: number) {
    return (await http.get<Message>(`/messages/${id}`)).data
  },
  async deleteMessage(id: number) {
    await http.delete(`/messages/${id}`)
  },
  /** Deletes several messages; the ones gone already are skipped. */
  async deleteMessages(ids: number[]) {
    return (await http.post<{ count: number }>('/messages/delete', { ids })).data.count
  },
  async markRead(id: number) {
    await http.post(`/messages/${id}/read`)
  },
  async markAllRead(topic: string | null, upToId: number | null) {
    return (await http.post<{ count: number }>('/messages/read', { topic, upToId })).data.count
  },
  /** Sends a message; the key makes a repeated send (double click, retry) store it only once. */
  async publish(form: FormData, idempotencyKey: string) {
    return (await http.post<Message>('/messages', form, { headers: { 'Idempotency-Key': idempotencyKey } })).data
  },
  async runAction(id: number, index: number) {
    return (await http.post<ActionResult>(`/messages/${id}/actions/${index}`)).data
  },

  async topics() {
    return (await http.get<Topic[]>('/topics')).data
  },
  async patchTopic(name: string, patch: TopicPatch) {
    return (await http.patch<Topic>(`/topics/${encodeURIComponent(name)}`, patch)).data
  },

  async publishTokens() {
    return (await http.get<PublishToken[]>('/publish-tokens')).data
  },
  async createPublishToken(name: string, topics: string[]) {
    return (await http.post<CreatedPublishToken>('/publish-tokens', { name, topics })).data
  },
  async deletePublishToken(id: string) {
    await http.delete(`/publish-tokens/${encodeURIComponent(id)}`)
  },

  async devices() {
    return (await http.get<Device[]>('/devices')).data
  },
  async setSubscriptions(id: string, topics: string[]) {
    return (await http.put<Device>(`/devices/${encodeURIComponent(id)}/subscriptions`, { topics })).data
  },
  async deleteDevice(id: string) {
    await http.delete(`/devices/${encodeURIComponent(id)}`)
  },
}
