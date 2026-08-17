import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    // Proxy API calls to the Admin API so the dev dashboard needs no CORS
    // exceptions beyond the explicit allow-list the API already carries, and so
    // the browser sees a single origin.
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, ''),
      },
    },
  },
})
