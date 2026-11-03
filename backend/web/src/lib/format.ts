const time = new Intl.DateTimeFormat('de-DE', { hour: '2-digit', minute: '2-digit' })
const dayAndTime = new Intl.DateTimeFormat('de-DE', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
const full = new Intl.DateTimeFormat('de-DE', { dateStyle: 'medium', timeStyle: 'short' })
const day = new Intl.DateTimeFormat('de-DE', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })

function isSameDay(a: Date, b: Date) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()
}

/** Short: the time today, otherwise day and time, in older years with the year. */
export function shortTime(iso: string, now = new Date()) {
  const date = new Date(iso)
  if (isSameDay(date, now)) return time.format(date)
  if (date.getFullYear() === now.getFullYear()) return dayAndTime.format(date)
  return full.format(date)
}

export function fullTime(iso: string) {
  return full.format(new Date(iso))
}

/** Heading over the messages of one day: "Heute", "Gestern" or the date. */
export function dayLabel(iso: string, now = new Date()) {
  const date = new Date(iso)
  if (isSameDay(date, now)) return 'Heute'
  const yesterday = new Date(now)
  yesterday.setDate(now.getDate() - 1)
  if (isSameDay(date, yesterday)) return 'Gestern'
  return day.format(date)
}

export function fileSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toLocaleString('de-DE', { maximumFractionDigits: 0 })} KB`
  return `${(bytes / 1024 / 1024).toLocaleString('de-DE', { maximumFractionDigits: 1 })} MB`
}

export const priorityLabels: Record<number, string> = {
  1: 'Minimal',
  2: 'Niedrig',
  3: 'Normal',
  4: 'Hoch',
  5: 'Dringend',
}
