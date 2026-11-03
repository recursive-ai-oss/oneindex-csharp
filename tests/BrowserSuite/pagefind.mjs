// Measures Pagefind (a released npm package, run as a black box) on the same abstracts as the browser suite, in the same Chromium,
// for the text queries of the suite's queries.json: payload before the first query, bytes fetched and latency per query.
//
//   node pagefind.mjs --corpus <abstracts.jsonl> --docs N --queries <queries.json> --pagefind <dir containing node_modules/pagefind>
//                     --work <scratch dir> [--chrome <path>] [--playwright <module dir>] [--report <file>]
//
// Each abstract becomes one HTML page (title as heading, abstract as paragraph), as a static site generator would produce it.
// A query is `pagefind.search(text)` followed by loading the data (url, excerpt, title) of the first ten results, which is what a
// search box shows; the server counts every byte it sends for the query.
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import zlib from 'node:zlib'
import { spawnSync } from 'node:child_process'
import { createRequire } from 'node:module'

const arg = (name, fallback) => { const i = process.argv.indexOf('--' + name); return i > 0 ? process.argv[i + 1] : fallback }
const corpus = arg('corpus'), count = parseInt(arg('docs', '3000')), queriesFile = arg('queries')
const pagefindDir = path.resolve(arg('pagefind')), work = path.resolve(arg('work'))
const chrome = arg('chrome', '/usr/bin/google-chrome'), reportFile = arg('report')
const require = createRequire(path.resolve(arg('playwright', '.')) + '/')
const { chromium } = require('playwright-core')

// Same selection as the fixture: the first N abstracts with text.
const site = path.join(work, 'site')
fs.rmSync(work, { recursive: true, force: true })
fs.mkdirSync(site, { recursive: true })
let n = 0
const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
for (const line of fs.readFileSync(corpus, 'utf8').split('\n')) {
  if (n >= count) break
  if (!line) continue
  const j = JSON.parse(line)
  if (!j.text) continue
  fs.writeFileSync(path.join(site, `w${j.id}.html`), `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>${esc(j.title ?? '')}</title></head><body data-pagefind-body><h1>${esc(j.title ?? '')}</h1><p>${esc(j.text)}</p></body></html>`)
  n++
}

const bin = path.join(pagefindDir, 'node_modules', '.bin', 'pagefind')
const t0 = Date.now()
const built = spawnSync(bin, ['--site', site], { encoding: 'utf8' })
if (built.status !== 0) { console.error(built.stdout, built.stderr); process.exit(1) }
const buildMs = Date.now() - t0
const out = path.join(site, 'pagefind')
const files = fs.readdirSync(out, { recursive: true }).filter((f) => fs.statSync(path.join(out, f)).isFile())
const sizeOf = (f) => fs.statSync(path.join(out, f)).size
const total = files.reduce((a, f) => a + sizeOf(f), 0)
const runtime = files.filter((f) => /^(pagefind\.js|pagefind-.*\.js|wasm\..*\.pagefind|pagefind_web_bg\.wasm)$/.test(f) || f.endsWith('.wasm'))
const gz = (b) => zlib.gzipSync(b, { level: 9 }).length
const br = (b) => zlib.brotliCompressSync(b, { params: { [zlib.constants.BROTLI_PARAM_QUALITY]: 11 } }).length
const runtimeRaw = runtime.reduce((a, f) => a + sizeOf(f), 0)
const runtimeBytes = Buffer.concat(runtime.map((f) => fs.readFileSync(path.join(out, f))))

let sent = { bytes: 0, requests: 0 }
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost')
  if (url.pathname === '/page.html') { res.writeHead(200, { 'content-type': 'text/html' }).end('<!doctype html><meta charset=utf-8>'); return }
  const file = path.resolve(site, '.' + decodeURIComponent(url.pathname))
  if (!file.startsWith(site + path.sep) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404).end(); return }
  const data = fs.readFileSync(file)
  sent.requests++; sent.bytes += data.length
  const type = file.endsWith('.js') ? 'text/javascript' : file.endsWith('.wasm') ? 'application/wasm' : 'application/octet-stream'
  res.writeHead(200, { 'content-type': type, 'content-length': data.length, 'cache-control': 'no-store' }).end(data)
})
await new Promise((r) => server.listen(0, '127.0.0.1', r))
const origin = `http://127.0.0.1:${server.address().port}`
const queries = JSON.parse(fs.readFileSync(queriesFile, 'utf8')).filter((q) => q.kind === 'text').map((q) => q.text)

const browser = await chromium.launch({ executablePath: chrome, headless: true, args: ['--no-sandbox'] })
const page = await (await browser.newContext()).newPage()
await page.goto(`${origin}/page.html`)
sent = { bytes: 0, requests: 0 }
const rows = []
const init = await page.evaluate(async () => {
  const t = performance.now()
  window.pf = await import('/pagefind/pagefind.js')
  await window.pf.init()
  return performance.now() - t
})
const initBytes = sent.bytes, initRequests = sent.requests
for (const q of queries) {
  sent = { bytes: 0, requests: 0 }
  const r = await page.evaluate(async (text) => {
    const t = performance.now()
    const s = await window.pf.search(text)
    const data = await Promise.all(s.results.slice(0, 10).map((x) => x.data()))
    return { ms: performance.now() - t, hits: s.results.length, loaded: data.length }
  }, q)
  rows.push({ q, ms: r.ms, hits: r.hits, bytes: sent.bytes, requests: sent.requests })
}
await browser.close(); server.close()
const ms = rows.map((r) => r.ms).sort((a, b) => a - b)
const report = {
  docs: n, buildMs, indexFiles: files.length, indexBytes: total,
  runtimeFiles: runtime, runtimeRawBytes: runtimeRaw, runtimeGzipBytes: gz(runtimeBytes), runtimeBrotliBytes: br(runtimeBytes),
  initMs: init, initBytes, initRequests,
  queries: rows.length, medianMs: ms[Math.floor(ms.length / 2)], p95Ms: ms[Math.min(ms.length - 1, Math.floor(ms.length * 0.95))],
  bytesPerQueryAvg: Math.round(rows.reduce((a, r) => a + r.bytes, 0) / rows.length), requestsPerQueryAvg: +(rows.reduce((a, r) => a + r.requests, 0) / rows.length).toFixed(2),
  firstQueryBytes: rows[0].bytes, rows,
}
const text = JSON.stringify(report, null, 1)
if (reportFile) fs.writeFileSync(reportFile, text)
console.log(text)
