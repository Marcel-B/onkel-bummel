import { parse, safeHref } from './markdown'

describe('parse', () => {
  it('keeps plain text as it is, HTML included', () => {
    expect(parse('<b>Tor</b> & offen')).toEqual([{ kind: 'text', text: '<b>Tor</b> & offen' }])
  })

  it('reads bold, italic, code and line breaks', () => {
    expect(parse('**Druck** ist *fertig*\n`42 %` _jetzt_')).toEqual([
      { kind: 'bold', children: [{ kind: 'text', text: 'Druck' }] },
      { kind: 'text', text: ' ist ' },
      { kind: 'italic', children: [{ kind: 'text', text: 'fertig' }] },
      { kind: 'break' },
      { kind: 'code', text: '42 %' },
      { kind: 'text', text: ' ' },
      { kind: 'italic', children: [{ kind: 'text', text: 'jetzt' }] },
    ])
  })

  it('leaves underscores inside words alone', () => {
    expect(parse('white_check_mark')).toEqual([{ kind: 'text', text: 'white_check_mark' }])
  })

  it('makes links of markdown links and bare addresses, without the full stop', () => {
    expect(parse('[Job](https://fil-o-mat/jobs/42) und https://grafana.example/d/1.')).toEqual([
      { kind: 'link', href: 'https://fil-o-mat/jobs/42', children: [{ kind: 'text', text: 'Job' }] },
      { kind: 'text', text: ' und ' },
      { kind: 'link', href: 'https://grafana.example/d/1', children: [{ kind: 'text', text: 'https://grafana.example/d/1' }] },
      { kind: 'text', text: '.' },
    ])
  })

  it('keeps links with other schemes as text', () => {
    expect(parse('[klick](javascript:alert(1))')).toEqual([{ kind: 'text', text: '[klick](javascript:alert(1)' }, { kind: 'text', text: ')' }])
    expect(safeHref('data:text/html,hallo')).toBeNull()
    expect(safeHref('mailto:marcel@example.de')).toBe('mailto:marcel@example.de')
  })

  it('takes nothing inside code as markdown', () => {
    expect(parse('`**nicht fett**`')).toEqual([{ kind: 'code', text: '**nicht fett**' }])
  })
})
