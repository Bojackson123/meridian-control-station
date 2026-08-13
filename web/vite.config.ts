/// <reference types="vitest/config" />
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

  // The console's tests. They exist because MCS-003 is verified by Test as well as by Inspection,
  // and what they assert is the state language: which shape, which fill and which chip a frame in
  // each state resolves to. That derivation is a pure function precisely so this needs no map and
  // no station -- a test that had to boot MapLibre to find out whether a stale vehicle shows its
  // age would be slow enough that it stopped being run.
  test: {
    // jsdom, not the default node environment, because the panel rows are React and the thing
    // worth asserting about them is what an operator would read off the screen. The marker icons
    // are deliberately not tested here: they are drawn on a canvas jsdom does not implement, and
    // the assertion that matters -- which icon a state selects -- lives on the descriptor instead.
    environment: 'jsdom',

    // No globals. `describe`/`it`/`expect` are imported in each file, so eslint needs no test
    // environment configured and a stray `expect` outside a test file is an unresolved import
    // rather than something that silently resolves.
    globals: false,

    include: ['src/**/*.test.{ts,tsx}'],

    // Vitest replaces CSS imports with an empty string by default, which is the right default --
    // a component test has no business asserting on a stylesheet and processing one per file is
    // cost for nothing. It is turned back on here for a single test: the palette lives in two
    // places out of necessity, `palette.test.ts` reads `index.css` back to check they agree, and
    // stubbed out that file is empty and the check passes vacuously.
    css: true,
  },
})
