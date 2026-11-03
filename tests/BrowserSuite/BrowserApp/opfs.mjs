// The functions JsOpfsHost imports: a directory of the origin-private file system, as whole-file operations.
const root = await navigator.storage.getDirectory()
const dir = await root.getDirectoryHandle('oneindex-browser-suite', { create: true })

export async function list() {
  const names = []
  for await (const [name] of dir.entries()) names.push(name)
  return names.join('\n')
}

export async function read(name) {
  const handle = await dir.getFileHandle(name)
  const bytes = new Uint8Array(await (await handle.getFile()).arrayBuffer())
  let text = ""
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000))
  return btoa(text)
}

export async function write(name, base64) {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))
  const handle = await dir.getFileHandle(name, { create: true })
  const writable = await handle.createWritable()
  await writable.write(bytes)
  await writable.close()
}

export async function remove(name) {
  try { await dir.removeEntry(name) } catch { /* already gone */ }
}
