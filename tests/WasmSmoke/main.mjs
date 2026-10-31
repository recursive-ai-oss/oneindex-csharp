// Starts the app in Node (and, with a page around it, in a browser): the process exit code is the app's.
import { dotnet } from './_framework/dotnet.js'

const { runMain, getAssemblyExports, getConfig } = await dotnet.withDiagnosticTracing(false).create()
const config = getConfig()
const exitCode = await runMain(config.mainAssemblyName, [])
process.exit(exitCode)
