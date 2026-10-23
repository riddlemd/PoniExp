/**
 * PoniExp Inbox — watch this machine's PoniExp conversations live.
 *   /poniexp [since]       modal browser: conversations → messages (default since 7d)
 *   /poniexp-feed [since]  ambient top-right card with the latest messages
 * Data: `poniexp-client log --json --bodies`, polled every 3 s while open.
 */
import { execFile } from 'node:child_process'
import { existsSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

const POLL_MS = 3000
const FINISHED = new Set(['done', 'failed'])

export function parseLog(text) {
  const out = []
  for (const line of String(text).split('\n')) {
    const s = line.trim()
    if (!s) continue
    try {
      const m = JSON.parse(s)
      if (m && m.type === 'message' && m.conversationId) out.push(m)
    } catch {
      // A truncated last line while the daemon is writing is expected; skip it.
    }
  }
  return out
}

export const peerOf = m => (m.direction === 'out' ? m.to : m.from)

const stamp = m => Date.parse(m.createdOn) || 0

export function groupConversations(messages) {
  const byId = new Map()
  for (const m of messages) {
    if (!byId.has(m.conversationId)) byId.set(m.conversationId, [])
    byId.get(m.conversationId).push(m)
  }
  const convs = []
  for (const [id, list] of byId) {
    list.sort((a, b) => stamp(a) - stamp(b) || (a.sequence ?? 0) - (b.sequence ?? 0))
    const last = list[list.length - 1]
    const subject = list.find(m => m.subject)?.subject ?? null
    convs.push({
      id,
      last: stamp(last),
      messages: list,
      peer: peerOf(list[0]),
      state: FINISHED.has(last.status) ? 'finished' : 'open',
      subject
    })
  }
  return convs.sort((a, b) => b.last - a.last)
}

export function wrap(text, width) {
  const w = Math.max(1, width)
  const lines = []
  for (const para of String(text ?? '').split('\n')) {
    let cur = ''
    for (const word of para.split(/\s+/).filter(Boolean)) {
      let rest = word
      while (rest.length > w) {
        if (cur) {
          lines.push(cur)
          cur = ''
        }
        lines.push(rest.slice(0, w))
        rest = rest.slice(w)
      }
      if (!cur) cur = rest
      else if (cur.length + 1 + rest.length <= w) cur += ' ' + rest
      else {
        lines.push(cur)
        cur = rest
      }
    }
    lines.push(cur)
  }
  return lines
}

export const clip = (s, n) => {
  const str = String(s ?? '')
  return str.length <= n ? str.padEnd(n) : str.slice(0, Math.max(0, n - 1)) + '…'
}

export const hhmm = ms => {
  const d = new Date(ms)
  return d.getTime() ? d.toTimeString().slice(0, 8) : '--:--:--'
}

export function clampSelection(sel, count) {
  return count <= 0 ? 0 : Math.min(Math.max(0, sel), count - 1)
}

/** Thread lines for a conversation; each is { text, tone } so render can colour it. */
export function threadLines(conv, width) {
  const lines = []
  for (const m of conv.messages) {
    const arrow = m.direction === 'out' ? '→' : '←'
    const head = `${arrow} ${hhmm(stamp(m))}  ${m.from} → ${m.to}${m.status ? `  [${m.status}]` : ''}`
    lines.push({ text: clip(head, width), tone: m.direction === 'out' ? 'out' : 'in' })
    const body = m.bodyDropped ? '(body dropped)' : m.body
    for (const l of wrap(body, width - 2)) lines.push({ text: '  ' + l, tone: 'body' })
    lines.push({ text: '', tone: 'body' })
  }
  return lines
}

function clientBin() {
  const local = join(homedir(), '.local', 'bin', 'poniexp-client')
  return existsSync(local) ? local : 'poniexp-client'
}

export function fetchMessages(since) {
  return new Promise(resolve => {
    execFile(clientBin(), ['log', '--json', '--bodies', '--since', since], { maxBuffer: 32 * 1024 * 1024, timeout: 10000 }, (err, stdout, stderr) => {
      if (err) resolve({ error: (stderr || err.message || String(err)).trim().split('\n')[0] })
      else resolve({ messages: parseLog(stdout) })
    })
  })
}

const SINCE_RE = /^\d+[smhd]$/

/** Scroll position for the open thread. `scroll === null` follows the newest message;
 *  `max` is the last offset render computed, since reducers never see the layout. */
export function scrollThread(scroll, max, delta) {
  const cur = Math.min(scroll ?? max, max)
  const next = Math.max(0, cur + delta)
  return next >= max ? null : next
}

let threadMax = 0

export const browserState = since => ({ since, convs: [], error: null, loaded: false, fetchedAt: 0, view: 'list', sel: 0, openId: null, scroll: null })

/** Full-view state opened straight onto one thread, seeded with the feed's data so it
 *  paints immediately; Esc then lands on that conversation in the list. */
export function openThreadState(convId, convs, since = '7d') {
  const idx = convs.findIndex(c => c.id === convId)
  return { ...browserState(since), convs, loaded: convs.length > 0, view: 'thread', openId: convId, sel: Math.max(0, idx) }
}

export default function register(sdk) {
  const { Box, Dialog, Overlay, React, Text, defineWidgetApp, h, isCtrl, updateWidget } = sdk

  function usePoll(app, since) {
    React.useEffect(() => {
      let alive = true
      const tick = () =>
        fetchMessages(since).then(res => {
          if (!alive) return
          updateWidget(app, s => ({ ...s, error: res.error ?? null, convs: res.messages ? groupConversations(res.messages) : s.convs, loaded: true, fetchedAt: Date.now() }))
        })
      tick()
      const id = setInterval(tick, POLL_MS)
      return () => {
        alive = false
        clearInterval(id)
      }
    }, [since])
  }

  function Poller({ app, since }) {
    usePoll(app, since)
    return null
  }

  const { openWidget } = sdk

  const browser = defineWidgetApp({
    id: 'poniexp',
    help: 'PoniExp Inbox: watch this machine’s conversations live (arg: since, e.g. 2d)',
    usage: 'usage: /poniexp [since]   e.g. /poniexp · /poniexp 2d · /poniexp 6h',
    init(arg) {
      const since = arg.trim() || '7d'
      if (!SINCE_RE.test(since)) return null
      return browserState(since)
    },
    reduce(state, { ch, key }) {
      const count = state.convs.length
      if (isCtrl(key, ch, 'c')) return null
      if (state.view === 'list') {
        if (key.escape || ch === 'q') return null
        if (key.upArrow || ch === 'k') return { ...state, sel: clampSelection(state.sel - 1, count) }
        if (key.downArrow || ch === 'j') return { ...state, sel: clampSelection(state.sel + 1, count) }
        if (key.home) return { ...state, sel: 0 }
        if (key.end) return { ...state, sel: clampSelection(count - 1, count) }
        if (key.return && count) {
          const conv = state.convs[clampSelection(state.sel, count)]
          return { ...state, view: 'thread', openId: conv.id, scroll: null }
        }
        return state
      }
      if (key.escape || key.leftArrow || ch === 'q' || key.backspace) return { ...state, view: 'list', openId: null, scroll: null }
      const step = key.pageUp || key.pageDown ? 10 : 1
      if (key.upArrow || ch === 'k' || key.pageUp) return { ...state, scroll: scrollThread(state.scroll, threadMax, -step) }
      if (key.downArrow || ch === 'j' || key.pageDown) return { ...state, scroll: scrollThread(state.scroll, threadMax, step) }
      if (key.home) return { ...state, scroll: threadMax > 0 ? 0 : null }
      if (key.end || ch === 'f') return { ...state, scroll: null }
      return state
    },
    render({ cols, rows, state, t }) {
      const width = Math.max(40, Math.min(cols - 4, 120))
      const inner = width - 6
      const bodyRows = Math.max(5, rows - 12)
      const status = state.error
        ? h(Text, { color: t.color.error }, clip(`error: ${state.error}`, inner))
        : h(Text, { color: t.color.muted }, clip(state.loaded ? `${state.convs.length} conversations · since ${state.since} · updated ${hhmm(state.fetchedAt)}` : 'loading…', inner))
      let content
      let hint
      let title = 'PoniExp Inbox'
      if (state.view === 'list') {
        hint = '↑↓ select · Enter open · Esc close'
        const sel = clampSelection(state.sel, state.convs.length)
        const start = Math.max(0, Math.min(sel - Math.floor(bodyRows / 2), state.convs.length - bodyRows))
        const shown = state.convs.slice(start, start + bodyRows)
        content = shown.length
          ? shown.map((c, i) => {
              const active = start + i === sel
              const mark = c.state === 'open' ? '●' : '○'
              const line = `${active ? '›' : ' '} ${mark} ${hhmm(c.last)}  ${clip(c.peer, 24)} ${String(c.messages.length).padStart(3)}  ${c.subject ?? '(no subject)'}`
              return h(Text, { key: c.id, bold: active, color: active ? t.color.primary : c.state === 'open' ? t.color.text : t.color.muted }, clip(line, inner))
            })
          : [h(Text, { key: 'none', color: t.color.muted }, state.loaded ? 'No conversations in this window.' : ' ')]
      } else {
        const conv = state.convs.find(c => c.id === state.openId)
        if (!conv) {
          content = [h(Text, { key: 'gone', color: t.color.muted }, 'Conversation no longer in the log window.')]
        } else {
          title = `${conv.peer} · ${conv.subject ?? conv.id.slice(0, 8)} · ${conv.state}`
          const lines = threadLines(conv, inner)
          const max = Math.max(0, lines.length - bodyRows)
          const off = state.scroll === null ? max : Math.min(state.scroll, max)
          threadMax = max
          content = lines.slice(off, off + bodyRows).map((l, i) =>
            h(Text, { key: i, color: l.tone === 'out' ? t.color.accent : l.tone === 'in' ? t.color.ok : t.color.text, bold: l.tone !== 'body' }, l.text || ' ')
          )
          const pos = state.scroll === null ? 'following' : `${off + 1}-${Math.min(off + bodyRows, lines.length)}/${lines.length}`
          hint = `↑↓ PgUp/PgDn scroll · End follow · Esc back   ${pos}`
        }
      }
      return h(
        Overlay,
        { backdrop: true, zone: 'center' },
        h(Dialog, { hint, title: clip(title, inner).trimEnd(), width }, h(Poller, { app: browser, since: state.since }), status, h(Box, { flexDirection: 'column', marginTop: 1 }, ...content))
      )
    }
  })

  const FEED_W = 46
  const feed = defineWidgetApp({
    id: 'poniexp-feed',
    help: 'PoniExp Inbox feed: latest messages top-right; click one to open its conversation',
    usage: 'usage: /poniexp-feed [since]',
    mode: 'ambient',
    zone: 'top-right',
    width: FEED_W,
    init(arg) {
      const since = arg.trim() || '1d'
      if (!SINCE_RE.test(since)) return null
      return { since, convs: [], error: null, loaded: false, fetchedAt: 0 }
    },
    reduce: (state, { ch, key }) => (key.escape || ch === 'q' ? null : state),
    render({ state, t }) {
      // Float cards lose a couple of columns to the rail edge; truncate rather than wrap.
      const inner = FEED_W - 8
      const recent = state.convs
        .flatMap(c => c.messages)
        .sort((a, b) => stamp(b) - stamp(a))
        .slice(0, 4)
      const rowsOut = []
      for (let i = 0; i < 4; i++) {
        const m = recent[i]
        if (!m) {
          rowsOut.push(h(Text, { key: i }, ' '))
          continue
        }
        const who = peerOf(m).split('/')[0]
        const line = `${hhmm(stamp(m)).slice(0, 5)} ${m.direction === 'out' ? '→' : '←'} ${who}: ${String(m.body ?? '').replace(/\s+/g, ' ')}`
        const open = () => openWidget(browser, openThreadState(m.conversationId, state.convs))
        rowsOut.push(
          h(Box, { key: i, onClick: open }, h(Text, { wrap: 'truncate', color: m.direction === 'out' ? t.color.accent : t.color.ok }, clip(line, inner)))
        )
      }
      const title = state.error ? 'PoniExp · error' : 'PoniExp'
      return h(Dialog, { title, width: FEED_W }, h(Poller, { app: feed, since: state.since }), ...rowsOut)
    }
  })
}
