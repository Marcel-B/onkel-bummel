/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

// The server is the one under "dotnet run" (http://localhost:8080). The login always goes through it, the
// cookie it sets also counts here, because cookies do not care about the port.
const server = process.env.API_URL ?? 'http://localhost:8080'

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    // The server serves the app from its wwwroot (see Web/WebInterface.cs).
    outDir: fileURLToPath(new URL('../src/OnkelBummel.Api/wwwroot', import.meta.url)),
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': { target: server, changeOrigin: true },
      '/auth': { target: server, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
  },
})
