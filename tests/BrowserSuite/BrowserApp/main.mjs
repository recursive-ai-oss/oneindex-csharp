// Boots the app in a page or in Node; the first argument after the script is the static index address.
import { dotnet } from './_framework/dotnet.js'

const { runMain, getConfig } = await dotnet.withDiagnosticTracing(false).create()
const config = getConfig()
const base = (typeof process !== 'undefined' && process.argv[2]) || new URLSearchParams(globalThis.location?.search ?? '').get('index')
const mode = (typeof process !== "undefined" && process.argv[3]) || new URLSearchParams(globalThis.location?.search ?? "").get("mode") || "http"
const code = await runMain(config.mainAssemblyName, [base, mode])
if (typeof process !== 'undefined') { process.exit(code) } else { globalThis.__oneindexExit = code }
