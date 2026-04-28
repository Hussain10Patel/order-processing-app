import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    host: true,          // MUST be true (or "0.0.0.0")
    port: 5173,
    strictPort: true,
    allowedHosts: "all"
  }
})