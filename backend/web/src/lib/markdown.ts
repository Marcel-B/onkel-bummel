/**
 * "Markdown light" of message texts: **bold**, *italic* or _italic_, `code`, [links](https://…), bare links
 * and line breaks. The result is a list of pieces the component renders as elements, so no text from a
 * message ever becomes HTML.
 */
export type Piece =
  | { kind: 'text'; text: string }
  | { kind: 'bold' | 'italic'; children: Piece[] }
  | { kind: 'code'; text: string }
  | { kind: 'link'; href: string; children: Piece[] }
  | { kind: 'break' }

// Order matters: code first (nothing inside it counts), then links, then emphasis.
const pattern =
  /`([^`\n]+)`|\[([^\]\n]+)\]\(([^)\s]+)\)|(https?:\/\/[^\s<>()]+[^\s<>().,;:!?'"])|\*\*([^*\n]+?)\*\*|\*([^*\n]+?)\*|(?<![\p{L}\p{N}])_([^_\n]+?)_(?![\p{L}\p{N}])|\n/gu

/** Only http(s) and mail links; javascript: and the like stay text. */
export function safeHref(href: string): string | null {
  try {
    const url = new URL(href)
    return ['http:', 'https:', 'mailto:'].includes(url.protocol) ? url.href : null
  } catch {
    return null
  }
}

export function parse(text: string): Piece[] {
  const pieces: Piece[] = []
  let last = 0
  for (const match of text.matchAll(pattern)) {
    const index = match.index
    if (index > last) pieces.push({ kind: 'text', text: text.slice(last, index) })
    last = index + match[0].length
    const [whole, code, label, href, bare, bold, star, underscore] = match
    if (code !== undefined) pieces.push({ kind: 'code', text: code })
    else if (label !== undefined && href !== undefined) {
      const safe = safeHref(href)
      pieces.push(safe ? { kind: 'link', href: safe, children: parse(label) } : { kind: 'text', text: whole })
    } else if (bare !== undefined) {
      const safe = safeHref(bare)
      pieces.push(safe ? { kind: 'link', href: safe, children: [{ kind: 'text', text: bare }] } : { kind: 'text', text: bare })
    } else if (bold !== undefined) pieces.push({ kind: 'bold', children: parse(bold) })
    else if (star !== undefined) pieces.push({ kind: 'italic', children: parse(star) })
    else if (underscore !== undefined) pieces.push({ kind: 'italic', children: parse(underscore) })
    else pieces.push({ kind: 'break' })
  }
  if (last < text.length) pieces.push({ kind: 'text', text: text.slice(last) })
  return pieces
}
