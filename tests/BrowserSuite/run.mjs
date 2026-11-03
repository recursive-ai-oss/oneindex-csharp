// The browser suite: serves the published BrowserApp and a static index over HTTP, opens the app in headless Chromium, and checks that
// what the browser prints equals what the server-side fixture computed for the same queries.
//
//   node run.mjs --app <AppBundle> --fixture <dir with static/ and expected.txt> [--chrome <path>] [--playwright <module dir>]
//                [--modes http,memory,opfs] [--report <file>]
//
// Exit code 0 when every mode matched, 1 otherwise. Per mode it reports the number of queries compared, the latency of each query,
// the bytes and requests per query, and the totals (the server counts bytes it sends, so the numbers do not depend on the browser).
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { pathToFileURL } from 'node:url'

const arg = (name, fallback) => { const i = process.argv.indexOf('--' + name); return i > 0 ? process.argv[i + 1] : fallback }
const appDir = path.resolve(arg('app'))
const fixture = path.resolve(arg('fixture'))
const chrome = arg('chrome', '/usr/bin/google-chrome')
const modes = arg('modes', 'http,memory,opfs').split(',')
const reportFile = arg('report')
const require = createRequire(path.resolve(arg('playwright', '.')) + '/')
const { chromium } = require('playwright-core')

const types = { '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json', '.html': 'text/html', '.dll': 'application/octet-stream', '.pdb': 'application/octet-stream' }
let sent = { bytes: 0, requests: 0, indexBytes: 0, appBytes: 0 }
let perFile = new Map() // index bytes by file (chunk suffix removed), for the report

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost')
  let root, rel
  if (url.pathname.startsWith('/app/')) { root = appDir; rel = url.pathname.slice(5) }
  else if (url.pathname.startsWith('/index/')) { root = path.join(fixture, 'static'); rel = url.pathname.slice(7) }
  else if (url.pathname === '/page.html') {
    res.writeHead(200, { 'content-type': 'text/html' })
    res.end('<!doctype html><meta charset=utf-8><script type="module" src="/app/main.mjs"></script>')
    return
  } else { res.writeHead(404).end(); return }
  const file = path.resolve(root, rel)
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404).end(); return }
  const data = fs.readFileSync(file)
  sent.requests++; sent.bytes += data.length
  if (root === appDir) sent.appBytes += data.length; else { sent.indexBytes += data.length; const f = rel.replace(/\.c\d+$/, ''); perFile.set(f, (perFile.get(f) ?? 0) + data.length) }
  res.writeHead(200, { 'content-type': types[path.extname(file)] ?? 'application/octet-stream', 'content-length': data.length, 'access-control-allow-origin': '*', 'cache-control': 'no-store' })
  res.end(data)
})
await new Promise((r) => server.listen(0, '127.0.0.1', r))
const origin = `http://127.0.0.1:${server.address().port}`

const expected = fs.readFileSync(path.join(fixture, 'expected.txt'), 'utf8').split('\n').filter(Boolean)
const browser = await chromium.launch({ executablePath: chrome, headless: true, args: ['--no-sandbox'] })
const report = []
let failed = false
for (const mode of modes) {
  sent = { bytes: 0, requests: 0, indexBytes: 0, appBytes: 0 }
  perFile = new Map()
  const context = await browser.newContext()
  const page = await context.newPage()
  const lines = []
  let done
  const finished = new Promise((r) => { done = r })
  page.on('console', (m) => { const t = m.text(); lines.push(t); if (t === 'DONE') done() })
  page.on('pageerror', (e) => { lines.push('PAGEERROR ' + e.message); done() })
  const started = Date.now()
  await page.goto(`${origin}/page.html?index=${encodeURIComponent(origin + '/index')}&mode=${mode}`)
  await Promise.race([finished, new Promise((_, rej) => setTimeout(() => rej(new Error('timeout')), 900000))])
  const wall = Date.now() - started
  const results = lines.filter((l) => l.startsWith('RESULT ')).map((l) => l.slice(7))
  const stats = lines.filter((l) => l.startsWith('STAT ')).map((l) => l.slice(5).split('|').map(Number))
  const total = lines.find((l) => l.startsWith('TOTAL ')) ?? ''
  // Memory and OPFS modes have no IVF file, so they skip the vector queries; compare the lines they produced.
  const byIndex = new Map(expected.map((l) => [l.slice(0, l.indexOf('|')), l]))
  let matched = 0, compared = 0
  const diffs = []
  for (const r of results) {
    const k = r.slice(0, r.indexOf('|'))
    compared++
    if (byIndex.get(k) === r) matched++; else diffs.push({ browser: r.slice(0, 200), server: (byIndex.get(k) ?? '').slice(0, 200) })
  }
  const latencies = stats.map((s) => s[1]).sort((a, b) => a - b)
  const median = latencies.length ? latencies[Math.floor(latencies.length / 2)] : 0
  const p95 = latencies.length ? latencies[Math.min(latencies.length - 1, Math.floor(latencies.length * 0.95))] : 0
  const bytesPer = stats.map((s) => s[2]); const reqPer = stats.map((s) => s[3])
  // Per query kind (the second field of a result line): median latency, average bytes and requests.
  const kinds = {}
  for (const r of results) {
    const [i, kind] = r.split('|')
    const st = stats.find((s) => s[0] === Number(i))
    if (!st || Number(i) === 0) continue // the first query pays for opening; it is reported as firstQueryBytes
    ;(kinds[kind] ??= []).push(st)
  }
  const byKind = Object.fromEntries(Object.entries(kinds).map(([k, v]) => {
    const ms = v.map((s) => s[1]).sort((a, b) => a - b)
    return [k, { n: v.length, medianMs: ms[Math.floor(ms.length / 2)], avgBytes: Math.round(v.reduce((a, s) => a + s[2], 0) / v.length), avgRequests: +(v.reduce((a, s) => a + s[3], 0) / v.length).toFixed(1) }]
  }))
  const topFiles = [...perFile.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8)
  const row = { mode, byKind, topFiles, queries: results.length, compared, matched, ok: compared > 0 && matched === compared && !lines.some((l) => l.startsWith('PAGEERROR')),
    wallMs: wall, medianMs: median, p95Ms: p95, bytesPerQueryAvg: bytesPer.length ? Math.round(bytesPer.reduce((a, b) => a + b, 0) / bytesPer.length) : 0,
    requestsPerQueryAvg: reqPer.length ? +(reqPer.reduce((a, b) => a + b, 0) / reqPer.length).toFixed(2) : 0, firstQueryBytes: bytesPer[0] ?? 0,
    total, indexBytesSent: sent.indexBytes, appPayloadBytes: sent.appBytes, diffs: diffs.slice(0, 3) }
  report.push(row)
  if (!row.ok) { failed = true; console.error('FAILED', JSON.stringify(row, null, 1), lines.filter((l) => /error|exception|PAGEERROR/i.test(l)).slice(0, 5)) }
  await context.close()
}
await browser.close(); server.close()
const text = JSON.stringify({ chrome: await (async () => '')(), report }, null, 1)
if (reportFile) fs.writeFileSync(reportFile, text)
console.log(text)
process.exit(failed ? 1 : 0)
