// Limits of the server (MessagePublisher, TopicNames); checked here too, so the form can say so before sending.
export const maxTitleLength = 250
export const maxBodyLength = 32_000
export const maxTags = 10
export const maxTagLength = 64
export const maxFiles = 10

const topicPattern = /^[a-z0-9_-]{1,64}$/
const reservedTopics = new Set(['api', 'auth', 'static', 'health', 'metrics'])

export interface Draft {
  topic: string
  title: string
  message: string
  priority: number
  tags: string
  click: string
}

export function emptyDraft(topic = ''): Draft {
  return { topic, title: '', message: '', priority: 3, tags: '', click: '' }
}

export function isValidTopic(topic: string) {
  return topicPattern.test(topic) && !reservedTopics.has(topic)
}

/** Tags as typed: separated by commas or spaces, without empty ones and doubles. */
export function parseTags(text: string) {
  return [...new Set(text.split(/[\s,]+/).map((t) => t.trim()).filter(Boolean))]
}

/** What is wrong with the draft, in the words of the form; null when it can go out. */
export function draftProblem(draft: Draft, files: File[]): string | null {
  const topic = draft.topic.trim()
  if (!topic) return 'Es fehlt das Topic.'
  if (!isValidTopic(topic)) return 'Topics bestehen aus a–z, 0–9, _ und -, höchstens 64 Zeichen.'
  if (!draft.message.trim() && files.length === 0) return 'Es fehlt der Text oder ein Anhang.'
  if (draft.title.trim().length > maxTitleLength) return `Der Titel ist länger als ${maxTitleLength} Zeichen.`
  if (draft.message.trim().length > maxBodyLength) return `Der Text ist länger als ${maxBodyLength} Zeichen.`
  const tags = parseTags(draft.tags)
  if (tags.length > maxTags || tags.some((t) => t.length > maxTagLength))
    return `Höchstens ${maxTags} Tags mit je höchstens ${maxTagLength} Zeichen.`
  if (draft.click.trim() && !/^https?:\/\//i.test(draft.click.trim())) return 'Der Link braucht http:// oder https://.'
  if (files.length > maxFiles) return `Höchstens ${maxFiles} Anhänge pro Nachricht.`
  return null
}

/** The draft as multipart/form-data, the way POST /messages takes it with attachments. */
export function publishForm(draft: Draft, files: File[]) {
  const form = new FormData()
  form.append('topic', draft.topic.trim())
  if (draft.title.trim()) form.append('title', draft.title.trim())
  if (draft.message.trim()) form.append('message', draft.message.trim())
  form.append('priority', String(draft.priority))
  const tags = parseTags(draft.tags)
  if (tags.length) form.append('tags', tags.join(','))
  if (draft.click.trim()) form.append('click', draft.click.trim())
  for (const file of files) form.append('file', file, file.name)
  return form
}
