import { splitTags } from './emojis'

it('puts known short names in front as emojis and keeps the rest as tags', () => {
  expect(splitTags(['warning', 'drucker', 'White_Check_Mark'])).toEqual({ emojis: '⚠️✅', rest: ['drucker'] })
})
