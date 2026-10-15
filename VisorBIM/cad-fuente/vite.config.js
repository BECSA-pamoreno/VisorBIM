import { defineConfig } from 'vite'

// base './' para que funcione en cualquier subcarpeta de GitHub Pages (p. ej. VisorBIM/cad/)
export default defineConfig({
  base: './',
  build: {
    outDir: 'dist',
    modulePreload: false,
    chunkSizeWarningLimit: 8000
  }
})
