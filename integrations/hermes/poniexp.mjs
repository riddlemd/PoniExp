/**
 * PoniExp Inbox — watch this machine's PoniExp conversations live, and the Hermes sessions
 * (gateway wake turns, subagents, chats) that handled them.
 *   /poniexp [since]          modal browser: conversations → messages → session (default since 7d)
 *   /poniexp-feed [since]     ambient top-right card with the latest messages
 *   /poniexp-session <id>     one Hermes session's live transcript
 * Data: `poniexp-client log --json --bodies` and Hermes' `state.db` (read-only), polled every 3 s.
 */
import { execFile } from 'node:child_process'
import { existsSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

const POLL_MS = 3000
const FINISHED = new Set(['done', 'failed'])
// A session with no turn lease still counts as running this long after its last activity:
// a parent waiting on delegate_task holds no lease of its own.
const ACTIVE_GRACE_S = 90

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

/** Thread lines for a conversation; each is { text, tone, sessionId? } so render can colour it.
 *  With a Hermes index, each message gets a line per session that acked, replied or sent it. */
export function threadLines(conv, width, idx = null) {
  const lines = []
  for (const m of conv.messages) {
    const arrow = m.direction === 'out' ? '→' : '←'
    const head = `${arrow} ${hhmm(stamp(m))}  ${m.from} → ${m.to}${m.status ? `  [${m.status}]` : ''}`
    lines.push({ text: clip(head, width), tone: m.direction === 'out' ? 'out' : 'in' })
    for (const l of idx?.byMessage.get(m.id) ?? []) {
      const s = idx.byId.get(l.sessionId)
      lines.push({ text: clip(`  ↻ ${l.kind === 'ack' ? 'taken by' : l.kind === 'reply' ? 'replied from' : 'sent from'} ${sessionLabel(s)}`, width), tone: 'session', sessionId: s.id })
    }
    const body = m.bodyDropped ? '(body dropped)' : m.body
    for (const l of wrap(body, width - 2)) lines.push({ text: '  ' + l, tone: 'body' })
    lines.push({ text: '', tone: 'body' })
  }
  return lines
}

/** Session ids in the order their lines appear in a conversation, for `s` to cycle through. */
export function threadSessions(conv, idx) {
  const out = []
  for (const m of conv?.messages ?? []) for (const l of idx?.byMessage.get(m.id) ?? []) if (!out.includes(l.sessionId)) out.push(l.sessionId)
  return out
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
const UNIT_S = { s: 1, m: 60, h: 3600, d: 86400 }
export const sinceSeconds = since => (SINCE_RE.test(since) ? parseInt(since, 10) * UNIT_S[since.slice(-1)] : 7 * 86400)

// ---- Hermes sessions (state.db, read-only) ----
// Everything that knows state.db's layout lives in this section; a Hermes update that
// changes it should break here and nowhere else.

const LINK_TOOLS = { mcp__poniexp__ack: 'ack', mcp__poniexp__reply: 'reply', mcp__poniexp__send: 'send' }

/** A PoniExp MCP result as Hermes stores it: `<untrusted_tool_result …>{"result": "<json>"}</…>`. */
export function parseToolResult(content) {
  try {
    let v = JSON.parse(stripWrapper(content))
    if (v && typeof v.result === 'string') v = JSON.parse(v.result)
    return v && typeof v === 'object' ? v : null
  } catch {
    return null
  }
}

/** Which session acked, replied or sent which PoniExp message. Rows: { session_id, tool_name, content, timestamp }. */
export function linksFromToolRows(rows) {
  const links = []
  for (const r of rows) {
    const kind = LINK_TOOLS[r.tool_name]
    if (!kind) continue
    const v = parseToolResult(r.content)
    for (const m of Array.isArray(v) ? v : v ? [v] : []) {
      if (typeof m?.id === 'string' && typeof m.conversationId === 'string') {
        links.push({ messageId: m.id, conversationId: m.conversationId, kind, sessionId: r.session_id, at: r.timestamp ?? 0 })
      }
    }
  }
  return links
}

export function sessionRunning(s, now) {
  if (s.ended_at) return false
  if (s.lease && s.lease > now) return true
  return now - (s.last_activity_at ?? 0) < ACTIVE_GRACE_S
}

/** Index sessions and links for render: running rolls up from children to parents. */
export function indexHermes(data, now) {
  const byId = new Map()
  for (const s of data?.sessions ?? []) byId.set(s.id, { ...s, children: [], running: sessionRunning(s, now) })
  for (const s of byId.values()) if (s.parent && byId.has(s.parent)) byId.get(s.parent).children.push(s.id)
  const roll = (s, seen = new Set()) => {
    if (seen.has(s.id)) return s.running
    seen.add(s.id)
    return s.children.reduce((r, c) => roll(byId.get(c), seen) || r, s.running)
  }
  for (const s of byId.values()) s.running = roll(s)
  const byMessage = new Map()
  const byConversation = new Map()
  for (const l of data?.links ?? []) {
    if (!byId.has(l.sessionId)) continue
    if (!byMessage.has(l.messageId)) byMessage.set(l.messageId, [])
    const list = byMessage.get(l.messageId)
    if (!list.some(x => x.sessionId === l.sessionId)) list.push(l)
    if (!byConversation.has(l.conversationId)) byConversation.set(l.conversationId, new Set())
    byConversation.get(l.conversationId).add(l.sessionId)
  }
  return { byId, byMessage, byConversation }
}

/** Sessions worth listing: PoniExp wake turns, sessions that touched a message, and their subagents. */
export function relevantSessions(idx) {
  const keep = new Set()
  for (const s of idx.byId.values()) if (String(s.chat_id ?? '').startsWith('poniexp-wake-')) keep.add(s.id)
  for (const ids of idx.byConversation.values()) for (const id of ids) keep.add(id)
  const stack = [...keep]
  while (stack.length) {
    for (const c of idx.byId.get(stack.pop())?.children ?? []) if (!keep.has(c)) keep.add(c) && stack.push(c)
  }
  const latest = s => Math.max(s.last_activity_at ?? 0, ...s.children.filter(c => keep.has(c)).map(c => latest(idx.byId.get(c))))
  const roots = [...keep].map(id => idx.byId.get(id)).filter(s => !keep.has(s.parent))
  const out = []
  const walk = s => {
    out.push(s)
    s.children.map(c => idx.byId.get(c)).sort((a, b) => (a.started_at ?? 0) - (b.started_at ?? 0)).forEach(walk)
  }
  roots.sort((a, b) => latest(b) - latest(a)).forEach(walk)
  return out
}

export const conversationRunning = (idx, convId) => [...(idx.byConversation.get(convId) ?? [])].some(id => idx.byId.get(id)?.running)

export function sessionLabel(s) {
  const kind = String(s.chat_id ?? '').startsWith('poniexp-wake-') ? 'wake' : s.source ?? 'session'
  const title = String(s.title ?? '').startsWith('[A2A inbound') ? '' : s.title ?? ''
  return `${kind} ${hhmm((s.started_at ?? 0) * 1000).slice(0, 5)}${title ? ` · ${title}` : ''} · ${s.running ? 'running' : 'idle'} · ${s.tool_call_count ?? 0} tools`
}

const stripWrapper = s =>
  String(s ?? '')
    .replace(/<untrusted_tool_result[^>]*>\s*(The following content[^\n]*\n)?/g, '')
    .replace(/<\/untrusted_tool_result>/g, '')
    .trim()

function describeArgs(name, args) {
  if (!args || typeof args !== 'object') return ''
  if (typeof args.command === 'string') return args.command
  if (typeof args.body === 'string') return `${args.messageId ? args.messageId.slice(0, 8) + ' ' : args.to ? args.to + ' ' : ''}${args.status ? `[${args.status}] ` : ''}${args.body}`
  if (typeof args.path === 'string') return args.path
  if (typeof args.goal === 'string') return args.goal
  if (Array.isArray(args.tasks)) return args.tasks.map(t => t.goal ?? '').join(' | ')
  return JSON.stringify(args)
}

/** One line per tool call; `tool_call` and `delegate_task` are unwrapped so the real tool shows. */
export function toolCallLines(toolCalls) {
  let calls
  try {
    calls = typeof toolCalls === 'string' ? JSON.parse(toolCalls) : toolCalls
  } catch {
    return []
  }
  const out = []
  for (const c of Array.isArray(calls) ? calls : []) {
    const name = c?.function?.name ?? '?'
    let args
    try {
      args = JSON.parse(c?.function?.arguments ?? '{}')
    } catch {
      args = {}
    }
    if (name === 'tool_call' && Array.isArray(args.calls)) {
      for (const inner of args.calls) out.push(`${inner.name ?? '?'}  ${describeArgs(inner.name, inner.arguments)}`)
    } else out.push(`${name}  ${describeArgs(name, args)}`)
  }
  return out.map(l => l.replace(/\s+/g, ' ').trim())
}

/** Transcript lines for a session view; each is { text, tone, sessionId? }. */
export function transcriptLines(rows, children, width) {
  const lines = []
  for (const c of children) lines.push({ text: clip(`⤷ subagent · ${sessionLabel(c)}`, width), tone: 'session', sessionId: c.id })
  if (children.length) lines.push({ text: '', tone: 'body' })
  for (const r of rows) {
    const at = hhmm((r.timestamp ?? 0) * 1000)
    if (r.role === 'user') {
      lines.push({ text: clip(`▶ ${at}  prompt`, width), tone: 'in' })
      wrap(stripWrapper(r.content), width - 2).slice(0, 6).forEach(l => lines.push({ text: '  ' + l, tone: 'body' }))
    } else if (r.role === 'assistant') {
      const text = String(r.content ?? '').trim()
      if (text) {
        lines.push({ text: clip(`◆ ${at}`, width), tone: 'out' })
        wrap(text, width - 2).forEach(l => lines.push({ text: '  ' + l, tone: 'body' }))
      }
      for (const t of toolCallLines(r.tool_calls)) lines.push({ text: clip(`↻ ${at}  ${t}`, width), tone: 'tool' })
    } else if (r.role === 'tool') {
      const first = stripWrapper(r.content).split('\n').find(l => l.trim()) ?? ''
      lines.push({ text: clip(`    ↳ ${r.tool_name ?? ''}: ${first.trim()}`, width), tone: 'muted' })
    }
  }
  return lines
}

export const stateDbPath = () => join(process.env.HERMES_HOME?.trim() || join(homedir(), '.hermes'), 'state.db')

let sqlite
async function openDb(path) {
  // node:sqlite is built into Node 22.5+; without it the widget still shows PoniExp traffic.
  sqlite ??= await import('node:sqlite').catch(() => null)
  if (!sqlite || !existsSync(path)) return null
  return new sqlite.DatabaseSync(path, { readOnly: true })
}

export async function readHermes(sinceS, path = stateDbPath(), now = Date.now() / 1000) {
  let db
  try {
    db = await openDb(path)
    if (!db) return null
    const from = now - sinceS
    const sessions = db
      .prepare(
        `SELECT s.id, s.parent_session_id AS parent, s.source, s.chat_id, s.title, s.started_at, s.ended_at,
                s.last_activity_at, s.last_activity_description AS activity, s.tool_call_count, l.expires_at AS lease
           FROM sessions s LEFT JOIN session_turn_leases l ON l.conversation_id = s.id
          WHERE s.last_activity_at >= ? OR s.started_at >= ?`
      )
      .all(from, from)
    const rows = db
      .prepare(
        `SELECT session_id, tool_name, substr(content, 1, 4000) AS content, timestamp FROM messages
          WHERE session_id IN (SELECT id FROM sessions WHERE last_activity_at >= ? OR started_at >= ?)
            AND role = 'tool' AND tool_name IN (${Object.keys(LINK_TOOLS).map(() => '?').join(', ')})`
      )
      .all(from, from, ...Object.keys(LINK_TOOLS))
    return { sessions, links: linksFromToolRows(rows) }
  } catch (e) {
    return { error: `state.db: ${e.message ?? e}` }
  } finally {
    db?.close()
  }
}

export async function readTranscript(sessionId, path = stateDbPath()) {
  let db
  try {
    db = await openDb(path)
    if (!db) return { rows: [] }
    const rows = db
      .prepare(
        `SELECT id, role, substr(content, 1, 6000) AS content, tool_calls, tool_name, timestamp FROM messages
          WHERE session_id = ? ORDER BY id`
      )
      .all(sessionId)
    return { rows }
  } catch (e) {
    return { rows: [], error: `state.db: ${e.message ?? e}` }
  } finally {
    db?.close()
  }
}

/** Scroll position for the open thread. `scroll === null` follows the newest message;
 *  `max` is the last offset render computed, since reducers never see the layout. */
export function scrollThread(scroll, max, delta) {
  const cur = Math.min(scroll ?? max, max)
  const next = Math.max(0, cur + delta)
  return next >= max ? null : next
}

// Reducers never see the layout; render records the last scroll limits here.
const layout = { threadMax: 0, sessionMax: 0 }

export const browserState = since => ({
  since,
  convs: [],
  error: null,
  loaded: false,
  fetchedAt: 0,
  view: 'list',
  sel: 0,
  openId: null,
  scroll: null,
  hermes: null,
  pick: -1,
  sessionsSel: 0,
  sessionId: null,
  transcript: null,
  back: []
})

/** Full-view state opened straight onto one thread, seeded with the feed's data so it
 *  paints immediately; Esc then lands on that conversation in the list. */
export function openThreadState(convId, convs, since = '7d', hermes = null) {
  const idx = convs.findIndex(c => c.id === convId)
  return { ...browserState(since), convs, hermes, loaded: convs.length > 0, view: 'thread', openId: convId, sel: Math.max(0, idx) }
}

/** Push the current view and show one session's transcript. */
export function openSession(state, sessionId) {
  if (!sessionId) return state
  const here = { view: state.view, sessionId: state.sessionId, scroll: state.scroll, pick: state.pick }
  return { ...state, back: [...state.back, here], view: 'session', sessionId, transcript: null, scroll: null, pick: -1 }
}

/** Pop back to the previous view; null (close) when there is nowhere to go back to. */
export function goBack(state) {
  if (!state.back.length) return state.view === 'thread' ? { ...state, view: 'list', openId: null, scroll: null, pick: -1 } : null
  const prev = state.back[state.back.length - 1]
  return { ...state, back: state.back.slice(0, -1), view: prev.view, sessionId: prev.sessionId, scroll: prev.scroll, pick: prev.pick, transcript: null }
}

export const sessionState = sessionId => ({ ...browserState('1d'), view: 'session', sessionId })

const hermesIndex = state => indexHermes(state.hermes, Date.now() / 1000)

/** What `s` cycles through and Enter opens in the current view. */
export function pickable(state, idx = hermesIndex(state)) {
  if (state.view === 'thread') return threadSessions(state.convs.find(c => c.id === state.openId), idx)
  if (state.view === 'session') return idx.byId.get(state.sessionId)?.children ?? []
  return []
}

export function reduceBrowser(state, { ch, key }, isCtrl = () => false) {
  if (isCtrl(key, ch, 'c')) return null
  const count = state.convs.length
  if (state.view === 'list') {
    if (key.escape || ch === 'q') return null
    if (key.upArrow || ch === 'k') return { ...state, sel: clampSelection(state.sel - 1, count) }
    if (key.downArrow || ch === 'j') return { ...state, sel: clampSelection(state.sel + 1, count) }
    if (key.home) return { ...state, sel: 0 }
    if (key.end) return { ...state, sel: clampSelection(count - 1, count) }
    if (ch === 'g') return { ...state, back: [...state.back, { view: 'list', sessionId: null, scroll: null, pick: -1 }], view: 'sessions', sessionsSel: 0 }
    if (key.return && count) {
      const conv = state.convs[clampSelection(state.sel, count)]
      return { ...state, view: 'thread', openId: conv.id, scroll: null, pick: -1 }
    }
    return state
  }
  if (state.view === 'sessions') {
    const list = relevantSessions(hermesIndex(state))
    if (key.escape || key.leftArrow || ch === 'q' || key.backspace) return goBack(state)
    if (key.upArrow || ch === 'k') return { ...state, sessionsSel: clampSelection(state.sessionsSel - 1, list.length) }
    if (key.downArrow || ch === 'j') return { ...state, sessionsSel: clampSelection(state.sessionsSel + 1, list.length) }
    if (key.return && list.length) return openSession(state, list[clampSelection(state.sessionsSel, list.length)].id)
    return state
  }
  // thread and session views scroll the same way and pick sessions with s / Enter.
  if (key.escape || key.leftArrow || ch === 'q' || key.backspace) return goBack(state)
  const max = state.view === 'thread' ? layout.threadMax : layout.sessionMax
  const step = key.pageUp || key.pageDown ? 10 : 1
  if (key.upArrow || ch === 'k' || key.pageUp) return { ...state, scroll: scrollThread(state.scroll, max, -step) }
  if (key.downArrow || ch === 'j' || key.pageDown) return { ...state, scroll: scrollThread(state.scroll, max, step) }
  if (key.home) return { ...state, scroll: max > 0 ? 0 : null }
  if (key.end || ch === 'f') return { ...state, scroll: null }
  const ids = pickable(state)
  if (ch === 's' && ids.length) return { ...state, pick: (state.pick + 1) % ids.length }
  if (key.return && ids.length) return openSession(state, ids[state.pick >= 0 ? state.pick : ids.length - 1])
  return state
}

export default function register(sdk) {
  const { Box, Dialog, Overlay, React, Text, defineWidgetApp, dismissWidget, h, isCtrl, openWidget, updateWidget } = sdk

  function usePoll(app, since) {
    React.useEffect(() => {
      let alive = true
      const tick = () =>
        Promise.all([fetchMessages(since), readHermes(sinceSeconds(since))]).then(([res, hermes]) => {
          if (!alive) return
          updateWidget(app, s =>
            s ? { ...s, error: res.error ?? null, convs: res.messages ? groupConversations(res.messages) : s.convs, hermes: hermes ?? s.hermes, loaded: true, fetchedAt: Date.now() } : s
          )
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

  function TranscriptPoller({ app, sessionId }) {
    React.useEffect(() => {
      let alive = true
      const tick = () =>
        readTranscript(sessionId).then(res => {
          if (alive) updateWidget(app, s => (s && s.sessionId === sessionId ? { ...s, transcript: { id: sessionId, ...res } } : s))
        })
      tick()
      const id = setInterval(tick, POLL_MS)
      return () => {
        alive = false
        clearInterval(id)
      }
    }, [sessionId])
    return null
  }

  const toneColor = (t, tone) =>
    ({ out: t.color.accent, in: t.color.ok, session: t.color.warn, tool: t.color.tool ?? t.color.primary, muted: t.color.muted })[tone] ?? t.color.text

  /** Scrolled window over lines; session lines click through and the picked one is bold. */
  function scrolled(app, lines, bodyRows, scroll, picked, t, which) {
    const max = Math.max(0, lines.length - bodyRows)
    const off = scroll === null ? max : Math.min(scroll, max)
    layout[which] = max
    const content = lines.slice(off, off + bodyRows).map((l, i) => {
      const text = h(Text, { color: toneColor(t, l.tone), bold: l.tone !== 'body' && l.tone !== 'muted', inverse: !!picked && l.sessionId === picked }, l.text || ' ')
      return l.sessionId ? h(Box, { key: i, onClick: () => updateWidget(app, s => (s ? openSession(s, l.sessionId) : s)) }, text) : h(Box, { key: i }, text)
    })
    const pos = scroll === null ? 'following' : `${off + 1}-${Math.min(off + bodyRows, lines.length)}/${lines.length}`
    return { content, pos }
  }

  function renderBrowser(app, { cols, rows, state, t }) {
    const width = Math.max(40, Math.min(cols - 4, 120))
    const inner = width - 6
    const bodyRows = Math.max(5, rows - 12)
    const idx = hermesIndex(state)
    const running = relevantSessions(idx).filter(s => s.running && !s.parent).length
    const hermesNote = state.hermes?.error ? ` · ${state.hermes.error}` : running ? ` · ↻ ${running} running` : ''
    const status = state.error
      ? h(Text, { color: t.color.error }, clip(`error: ${state.error}`, inner))
      : h(Text, { color: t.color.muted }, clip(state.loaded ? `${state.convs.length} conversations · since ${state.since} · updated ${hhmm(state.fetchedAt)}${hermesNote}` : 'loading…', inner))
    let content
    let hint
    let title = 'PoniExp Inbox'
    if (state.view === 'list') {
      hint = '↑↓ select · Enter open · g sessions · Esc close'
      const sel = clampSelection(state.sel, state.convs.length)
      const start = Math.max(0, Math.min(sel - Math.floor(bodyRows / 2), state.convs.length - bodyRows))
      const shown = state.convs.slice(start, start + bodyRows)
      content = shown.length
        ? shown.map((c, i) => {
            const active = start + i === sel
            const busy = conversationRunning(idx, c.id)
            const mark = busy ? '↻' : c.state === 'open' ? '●' : '○'
            const line = `${active ? '›' : ' '} ${mark} ${hhmm(c.last)}  ${clip(c.peer, 24)} ${String(c.messages.length).padStart(3)}  ${c.subject ?? '(no subject)'}`
            return h(Text, { key: c.id, bold: active, color: active ? t.color.primary : busy ? t.color.warn : c.state === 'open' ? t.color.text : t.color.muted }, clip(line, inner))
          })
        : [h(Text, { key: 'none', color: t.color.muted }, state.loaded ? 'No conversations in this window.' : ' ')]
    } else if (state.view === 'sessions') {
      title = 'Hermes sessions · PoniExp work'
      hint = '↑↓ select · Enter open · Esc back'
      const list = relevantSessions(idx)
      const sel = clampSelection(state.sessionsSel, list.length)
      const start = Math.max(0, Math.min(sel - Math.floor(bodyRows / 2), list.length - bodyRows))
      content = list.length
        ? list.slice(start, start + bodyRows).map((s, i) => {
            const active = start + i === sel
            const line = `${active ? '›' : ' '} ${s.running ? '↻' : '·'} ${s.parent ? '  ⤷ ' : ''}${sessionLabel(s)}`
            return h(Box, { key: s.id, onClick: () => updateWidget(app, st => (st ? openSession(st, s.id) : st)) }, h(Text, { bold: active, color: active ? t.color.primary : s.running ? t.color.warn : t.color.muted }, clip(line, inner)))
          })
        : [h(Text, { key: 'none', color: t.color.muted }, state.hermes ? 'No PoniExp sessions in this window.' : 'Hermes state.db not readable (needs Node 22.5+).')]
    } else if (state.view === 'thread') {
      const conv = state.convs.find(c => c.id === state.openId)
      if (!conv) {
        content = [h(Text, { key: 'gone', color: t.color.muted }, 'Conversation no longer in the log window.')]
      } else {
        title = `${conv.peer} · ${conv.subject ?? conv.id.slice(0, 8)} · ${conv.state}`
        const ids = pickable(state, idx)
        const r = scrolled(app, threadLines(conv, inner, idx), bodyRows, state.scroll, ids[state.pick], t, 'threadMax')
        content = r.content
        hint = `↑↓ scroll · ${ids.length ? 's pick session · Enter open · ' : ''}End follow · Esc back   ${r.pos}`
      }
    } else {
      const s = idx.byId.get(state.sessionId)
      title = s ? `↻ ${sessionLabel(s)}` : `session ${state.sessionId}`
      const tr = state.transcript?.id === state.sessionId ? state.transcript : null
      const children = (s?.children ?? []).map(id => idx.byId.get(id))
      const ids = pickable(state, idx)
      const r = scrolled(app, tr ? transcriptLines(tr.rows, children, inner) : [{ text: 'loading…', tone: 'muted' }], bodyRows, state.scroll, ids[state.pick], t, 'sessionMax')
      content = r.content
      hint = `↑↓ scroll · ${ids.length ? 's pick subagent · Enter open · ' : ''}End follow · Esc back   ${r.pos}`
      const detail = tr?.error ?? `${state.sessionId}${s?.activity ? ` · ${s.activity}` : ''}${tr ? ` · ${tr.rows.length} messages` : ''}`
      return h(
        Overlay,
        { backdrop: true, zone: 'center' },
        h(
          Dialog,
          { hint, title: clip(title, inner).trimEnd(), width },
          h(Poller, { app, since: state.since }),
          h(TranscriptPoller, { app, sessionId: state.sessionId }),
          h(Text, { color: tr?.error ? t.color.error : t.color.muted }, clip(detail, inner)),
          h(Box, { flexDirection: 'column', marginTop: 1 }, ...content)
        )
      )
    }
    return h(
      Overlay,
      { backdrop: true, zone: 'center' },
      h(Dialog, { hint, title: clip(title, inner).trimEnd(), width }, h(Poller, { app, since: state.since }), status, h(Box, { flexDirection: 'column', marginTop: 1 }, ...content))
    )
  }

  const browser = defineWidgetApp({
    id: 'poniexp',
    help: 'PoniExp Inbox: conversations, and the Hermes sessions working on them (arg: since, e.g. 2d)',
    usage: 'usage: /poniexp [since]   e.g. /poniexp · /poniexp 2d · /poniexp 6h',
    init(arg) {
      const since = arg.trim() || '7d'
      if (!SINCE_RE.test(since)) return null
      return browserState(since)
    },
    reduce: (state, input) => reduceBrowser(state, input, isCtrl),
    render: props => renderBrowser(browser, props)
  })

  const sessionApp = defineWidgetApp({
    id: 'poniexp-session',
    help: 'One Hermes session’s live transcript (gateway wake turns, subagents, chats)',
    usage: 'usage: /poniexp-session <session id>   (ids: /poniexp then g)',
    init(arg) {
      const id = arg.trim()
      return id ? sessionState(id) : null
    },
    reduce: (state, input) => reduceBrowser(state, input, isCtrl),
    render: props => renderBrowser(sessionApp, props)
  })

  const FEED_W = 46
  const feed = defineWidgetApp({
    id: 'poniexp-feed',
    help: 'PoniExp Inbox feed: latest messages top-right (↻ = a session is working on it); click one to open',
    usage: 'usage: /poniexp-feed [since]',
    mode: 'ambient',
    zone: 'top-right',
    width: FEED_W,
    init(arg) {
      const since = arg.trim() || '1d'
      if (!SINCE_RE.test(since)) return null
      return { since, convs: [], hermes: null, error: null, loaded: false, fetchedAt: 0 }
    },
    reduce: (state, { ch, key }) => (key.escape || ch === 'q' ? null : state),
    render({ state, t }) {
      // Float cards lose a couple of columns to the rail edge; truncate rather than wrap.
      const inner = FEED_W - 8
      const idx = hermesIndex(state)
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
        const busy = conversationRunning(idx, m.conversationId)
        const line = `${busy ? '↻' : ' '}${hhmm(stamp(m)).slice(0, 5)} ${m.direction === 'out' ? '→' : '←'} ${who}: ${String(m.body ?? '').replace(/\s+/g, ' ')}`
        const open = () => openWidget(browser, openThreadState(m.conversationId, state.convs, '7d', state.hermes))
        rowsOut.push(
          h(Box, { key: i, onClick: open }, h(Text, { wrap: 'truncate', color: m.direction === 'out' ? t.color.accent : t.color.ok }, clip(line, inner)))
        )
      }
      const running = relevantSessions(idx).filter(s => s.running && !s.parent).length
      const title = state.error ? 'PoniExp · error' : running ? `PoniExp · ↻ ${running} running` : 'PoniExp'
      // Hermes builds without dismissWidget can only toggle the card off with /poniexp-feed.
      const close = dismissWidget ? h(Box, { onClick: () => dismissWidget(feed) }, h(Text, { bold: true, color: t.color.muted }, '×')) : null
      return h(
        Box,
        { borderColor: t.color.primary, borderStyle: 'round', flexDirection: 'column', opaque: true, paddingX: 2, paddingBottom: 1, width: FEED_W },
        h(Box, { justifyContent: 'space-between', marginBottom: 1, width: inner }, h(Text, { bold: true, color: t.color.primary }, title), close),
        h(Poller, { app: feed, since: state.since }),
        ...rowsOut
      )
    }
  })
}
