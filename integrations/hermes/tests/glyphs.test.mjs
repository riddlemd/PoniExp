import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'

// hermes-ink measures U+2600–U+27BF as 2 cells, but terminals draw them in 1 without VS16,
// so any of them in a bordered card shifts the border and leaves stale characters behind.
test('Given_the_widget_source_When_scanned_for_dingbat_range_symbols_Then_none_are_present', () => {
  // Given
  const source = readFileSync(new URL('../poniexp.mjs', import.meta.url), 'utf8')

  // When
  const found = [...source].filter(c => { const cp = c.codePointAt(0); return cp >= 0x2600 && cp <= 0x27bf })

  // Then
  assert.deepEqual(found, [])
})
