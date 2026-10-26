import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'

// Built output is served by Tempo.Api as static files.
export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      registerType: 'autoUpdate',
      manifest: {
        name: 'TEMPO',
        short_name: 'TEMPO',
        description: 'Proactive time-keeper: leave on time, stops included.',
        display: 'standalone',
        background_color: '#FFFFFF',
        theme_color: '#1F5EFF',
        icons: [
          { src: 'icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: 'icon-512.png', sizes: '512x512', type: 'image/png' },
        ],
      },
    }),
  ],
  build: {
    outDir: '../Tempo.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/health': 'http://localhost:5181',
      '/api': 'http://localhost:5181',
    },
  },
})
