import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],

  // MapLibre starts its worker with `{ type: 'module' }`, so the chunk Vite emits for it has to be
  // one. The default here is an IIFE, which survives by accident today only because the bundled
  // worker happens to carry no import statements -- not a property worth depending on.
  worker: {
    format: 'es',
  },

  // The console fetches /api from its own origin, in dev and in compose alike. Dev is the only
  // place the two are really separate processes, so the dev server bridges them here rather than
  // the API carrying a CORS policy that only an IsDevelopment() check keeps out of production.
  // nginx takes this position in the deployed stack and follows the same rule.
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5271',

        // Server-sent events die behind a proxy that buffers, and arrive in bursts behind one
        // that compresses. Vite's proxy streams by default; this is here so the next person to
        // add a rule sees that it matters.
        ws: false,
      },
    },
  },
})
