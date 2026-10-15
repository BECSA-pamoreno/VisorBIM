// Copia los workers (y el wasm de LibreDWG) a public/workers para que Vite los sirva tal cual.
import { copyFileSync, mkdirSync } from 'node:fs'
import { resolve } from 'node:path'

const dest = resolve('public/workers')
mkdirSync(dest, { recursive: true })

const files = [
  ['node_modules/@mlightcad/cad-simple-viewer/dist/mtext-renderer-worker.js', 'mtext-renderer-worker.js'],
  ['node_modules/@mlightcad/libredwg-converter/dist/libredwg-parser-worker.js', 'libredwg-parser-worker.js'],
  ['node_modules/@mlightcad/libredwg-converter/dist/libredwg-web.wasm', 'libredwg-web.wasm']
]
for (const [src, name] of files) {
  copyFileSync(resolve(src), resolve(dest, name))
  console.log('  copiado', name)
}
