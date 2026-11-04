import { describe, expect, it } from 'vitest'
import { draftProblem, emptyDraft, isValidTopic, parseTags, publishForm } from './compose'

describe('isValidTopic', () => {
  it('takes what the server takes', () => {
    expect(isValidTopic('garage')).toBe(true)
    expect(isValidTopic('fil-o-mat_2')).toBe(true)
    expect(isValidTopic('Garage')).toBe(false)
    expect(isValidTopic('garage tor')).toBe(false)
    expect(isValidTopic('api')).toBe(false)
    expect(isValidTopic('x'.repeat(65))).toBe(false)
  })
})

describe('parseTags', () => {
  it('splits at commas and spaces and drops doubles', () => {
    expect(parseTags(' warning, tor  tor,,garage ')).toEqual(['warning', 'tor', 'garage'])
    expect(parseTags('  ')).toEqual([])
  })
})

describe('draftProblem', () => {
  const file = new File(['x'], 'bild.png', { type: 'image/png' })

  it('wants a valid topic and text or an attachment', () => {
    expect(draftProblem(emptyDraft(), [])).toBe('Es fehlt das Topic.')
    expect(draftProblem({ ...emptyDraft('Garage'), message: 'x' }, [])).toMatch(/a–z/)
    expect(draftProblem(emptyDraft('garage'), [])).toBe('Es fehlt der Text oder ein Anhang.')
    expect(draftProblem(emptyDraft('garage'), [file])).toBeNull()
    expect(draftProblem({ ...emptyDraft('garage'), message: 'Hallo' }, [])).toBeNull()
  })

  it('checks the link and the limits', () => {
    const draft = { ...emptyDraft('garage'), message: 'Hallo' }
    expect(draftProblem({ ...draft, click: 'example.com' }, [])).toMatch(/https/)
    expect(draftProblem({ ...draft, click: 'https://example.com' }, [])).toBeNull()
    expect(draftProblem({ ...draft, tags: Array.from({ length: 11 }, (_, i) => `t${i}`).join(',') }, [])).toMatch(/Tags/)
    expect(draftProblem(draft, Array.from({ length: 11 }, () => file))).toMatch(/Anhänge/)
  })
})

describe('publishForm', () => {
  it('leaves out empty fields and adds the files', () => {
    const file = new File(['x'], 'bild.png', { type: 'image/png' })
    const form = publishForm({ ...emptyDraft(' garage '), message: ' Hallo ', priority: 4, tags: 'warning tor' }, [file])

    expect(form.get('topic')).toBe('garage')
    expect(form.get('message')).toBe('Hallo')
    expect(form.get('priority')).toBe('4')
    expect(form.get('tags')).toBe('warning,tor')
    expect(form.has('title')).toBe(false)
    expect(form.has('click')).toBe(false)
    expect((form.getAll('file')[0] as File).name).toBe('bild.png')
  })
})
