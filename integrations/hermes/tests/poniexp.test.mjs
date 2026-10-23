import assert from 'node:assert/strict'
import { describe, test } from 'node:test'

import { clampSelection, clip, groupConversations, openThreadState, parseLog, peerOf, scrollThread, threadLines, wrap } from '../poniexp.mjs'

const msg = (o) => ({ type: 'message', direction: 'in', from: 'wojtek/a', to: 'ise/a', status: null, subject: null, body: 'x', sequence: 1, ...o })

describe('parseLog', () => {
  test('Given log lines with a truncated line and a non-message, When parsed, Then only whole messages are kept', () => {
    // Given
    const text = [JSON.stringify(msg({ id: '1', conversationId: 'c1' })), '{"id":"2","type":"mess', JSON.stringify({ type: 'receipt', conversationId: 'c1' }), ''].join('\n')

    // When
    const out = parseLog(text)

    // Then
    assert.deepEqual(out.map(m => m.id), ['1'])
  })

  test('Given a message without a conversation id, When parsed, Then it is dropped', () => {
    // Given
    const text = JSON.stringify(msg({ id: '1' }))

    // When
    const out = parseLog(text)

    // Then
    assert.equal(out.length, 0)
  })
})

describe('peerOf', () => {
  test('Given an outgoing message, When asking its peer, Then it is the recipient', () => {
    // Given
    const m = msg({ direction: 'out', from: 'ise/a', to: 'wojtek/a' })

    // When
    const peer = peerOf(m)

    // Then
    assert.equal(peer, 'wojtek/a')
  })

  test('Given an incoming message, When asking its peer, Then it is the sender', () => {
    // Given
    const m = msg({ direction: 'in', from: 'blinky/a', to: 'ise/a' })

    // When
    const peer = peerOf(m)

    // Then
    assert.equal(peer, 'blinky/a')
  })
})

describe('groupConversations', () => {
  const messages = [
    msg({ id: 'b2', conversationId: 'B', createdOn: '2026-10-23T17:40:00Z', status: 'done' }),
    msg({ id: 'a1', conversationId: 'A', createdOn: '2026-10-23T17:30:00Z', subject: 'ping' }),
    msg({ id: 'b1', conversationId: 'B', createdOn: '2026-10-23T17:35:00Z', direction: 'out', from: 'ise/a', to: 'blinky/a', subject: 'wake' })
  ]

  test('Given messages from two conversations, When grouped, Then the most recently active conversation comes first', () => {
    // Given / When
    const convs = groupConversations(messages)

    // Then
    assert.deepEqual(convs.map(c => c.id), ['B', 'A'])
  })

  test('Given out-of-order messages, When grouped, Then each thread is oldest first', () => {
    // Given / When
    const [b] = groupConversations(messages)

    // Then
    assert.deepEqual(b.messages.map(m => m.id), ['b1', 'b2'])
  })

  test('Given a thread whose last message is done, When grouped, Then it is finished', () => {
    // Given / When
    const [b] = groupConversations(messages)

    // Then
    assert.equal(b.state, 'finished')
  })

  test('Given a thread whose last message has no final status, When grouped, Then it is open', () => {
    // Given / When
    const [, a] = groupConversations(messages)

    // Then
    assert.equal(a.state, 'open')
  })

  test('Given a thread with a subject only on its first message, When grouped, Then it keeps that subject and the first message peer', () => {
    // Given / When
    const [b] = groupConversations(messages)

    // Then
    assert.equal(b.subject, 'wake')
    assert.equal(b.peer, 'blinky/a')
  })

  test('Given two messages with the same timestamp, When grouped, Then sequence breaks the tie', () => {
    // Given
    const same = [
      msg({ id: 'second', conversationId: 'C', createdOn: '2026-10-23T17:00:00Z', sequence: 5 }),
      msg({ id: 'first', conversationId: 'C', createdOn: '2026-10-23T17:00:00Z', sequence: 2 })
    ]

    // When
    const [c] = groupConversations(same)

    // Then
    assert.deepEqual(c.messages.map(m => m.id), ['first', 'second'])
  })
})

describe('wrap', () => {
  test('Given text longer than the width, When wrapped, Then no line exceeds the width and words stay whole', () => {
    // Given
    const text = 'pong from ise hermes default'

    // When
    const lines = wrap(text, 10)

    // Then
    assert.deepEqual(lines, ['pong from', 'ise hermes', 'default'])
  })

  test('Given a word longer than the width, When wrapped, Then it is split into width-sized pieces', () => {
    // Given
    const text = 'abcdefghij'

    // When
    const lines = wrap(text, 4)

    // Then
    assert.deepEqual(lines, ['abcd', 'efgh', 'ij'])
  })

  test('Given a word one longer than the width, When wrapped, Then its last character goes on its own line', () => {
    // Given
    const text = 'abcde'

    // When
    const lines = wrap(text, 4)

    // Then
    assert.deepEqual(lines, ['abcd', 'e'])
  })

  test('Given text with a newline, When wrapped, Then paragraphs stay on separate lines', () => {
    // Given
    const text = 'a\nb'

    // When
    const lines = wrap(text, 40)

    // Then
    assert.deepEqual(lines, ['a', 'b'])
  })
})

describe('clip', () => {
  test('Given a string longer than the limit, When clipped, Then it ends with an ellipsis at exactly the limit', () => {
    // Given / When
    const out = clip('abcdefgh', 5)

    // Then
    assert.equal(out, 'abcd…')
  })

  test('Given a string exactly the limit long, When clipped, Then it is shown whole', () => {
    // Given / When
    const out = clip('abcde', 5)

    // Then
    assert.equal(out, 'abcde')
  })

  test('Given a short string, When clipped, Then it is padded to the limit so rows keep their width', () => {
    // Given / When
    const out = clip('ab', 5)

    // Then
    assert.equal(out, 'ab   ')
  })
})

describe('clampSelection', () => {
  test('Given a selection past the end, When clamped, Then it stops on the last item', () => {
    // Given / When / Then
    assert.equal(clampSelection(5, 3), 2)
  })

  test('Given a selection above the top, When clamped, Then it stops on the first item', () => {
    // Given / When / Then
    assert.equal(clampSelection(-1, 3), 0)
  })

  test('Given an empty list, When clamped, Then it is zero', () => {
    // Given / When / Then
    assert.equal(clampSelection(4, 0), 0)
  })
})

describe('scrollThread', () => {
  test('Given a thread following the newest message, When scrolling up one, Then it sits one line above the bottom', () => {
    // Given / When
    const next = scrollThread(null, 20, -1)

    // Then
    assert.equal(next, 19)
  })

  test('Given a thread scrolled up, When scrolling down to the bottom, Then it follows again', () => {
    // Given / When
    const next = scrollThread(19, 20, 1)

    // Then
    assert.equal(next, null)
  })

  test('Given a thread near the top, When paging up past it, Then it stops at the first line', () => {
    // Given / When
    const next = scrollThread(3, 20, -10)

    // Then
    assert.equal(next, 0)
  })

  test('Given a thread scrolled up, When scrolling down but not to the bottom, Then it keeps its position', () => {
    // Given / When
    const next = scrollThread(5, 20, 1)

    // Then
    assert.equal(next, 6)
  })
})

describe('threadLines', () => {
  test('Given an outgoing done message, When laid out, Then the header shows direction, route and status and the body is indented', () => {
    // Given
    const conv = { messages: [msg({ direction: 'out', from: 'ise/a', to: 'wojtek/a', status: 'done', body: 'pong', createdOn: '2026-10-23T17:36:05Z' })] }

    // When
    const lines = threadLines(conv, 60)

    // Then
    assert.match(lines[0].text, /^→ \d\d:\d\d:\d\d  ise\/a → wojtek\/a  \[done\]/)
    assert.equal(lines[0].tone, 'out')
    assert.equal(lines[1].text, '  pong')
  })

  test('Given a message whose body was dropped, When laid out, Then it says so instead of showing nothing', () => {
    // Given
    const conv = { messages: [msg({ bodyDropped: true, body: null, createdOn: '2026-10-23T17:36:05Z' })] }

    // When
    const lines = threadLines(conv, 60)

    // Then
    assert.equal(lines[1].text, '  (body dropped)')
  })
})

describe('openThreadState', () => {
  const convs = [{ id: 'A', messages: [] }, { id: 'B', messages: [] }]

  test('Given a clicked feed message, When the full view opens, Then it shows that conversation following the newest message', () => {
    // Given / When
    const s = openThreadState('B', convs)

    // Then
    assert.equal(s.view, 'thread')
    assert.equal(s.openId, 'B')
    assert.equal(s.scroll, null)
  })

  test('Given a clicked feed message, When the full view opens, Then the list selection sits on that conversation for Esc', () => {
    // Given / When
    const s = openThreadState('B', convs)

    // Then
    assert.equal(s.sel, 1)
  })

  test('Given the feed already has data, When the full view opens, Then it is seeded with it instead of showing loading', () => {
    // Given / When
    const s = openThreadState('A', convs)

    // Then
    assert.equal(s.convs, convs)
    assert.equal(s.loaded, true)
  })

  test('Given a conversation id not in the data, When the full view opens, Then the selection falls back to the first row', () => {
    // Given / When
    const s = openThreadState('Z', convs)

    // Then
    assert.equal(s.sel, 0)
  })

  test('Given no window is passed, When the full view opens, Then it polls the default seven days', () => {
    // Given / When
    const s = openThreadState('A', convs)

    // Then
    assert.equal(s.since, '7d')
  })
})
