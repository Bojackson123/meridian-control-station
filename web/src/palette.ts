/**
 * The console's colours, once.
 *
 * The state language is a set of decisions about luminance before it is a set of hues, and the
 * ratios in `docs/notes/console-design.md` §2 were measured against these exact values. Anything
 * that draws — a canvas marker, an inline SVG badge, a stylesheet — takes them from here or from
 * the custom properties in `index.css` that mirror this file, and `palette.test.ts` fails if the
 * two ever disagree.
 *
 * That guard exists because the failure it prevents is the one the design note opens with: three
 * different greys meaning three different things, arrived at honestly, one file at a time.
 */

/** The map background. Every contrast ratio quoted below is measured against this. */
export const MAP_BACKGROUND = '#080c12'

/** The panel surface, 1.05:1 off the map background — near enough that one set of ratios governs both. */
export const PANEL_SURFACE = '#0d131b'

/** Live · 17.6:1. The brightest thing on screen, and the only thing allowed to be. */
export const INK_BRIGHT = '#eef3f8'

/** Resting panel values · 7.8:1. */
export const INK = '#93a6b8'

/** Lost, units and labels · 4.9:1 — the dimmest state, and still clear of the graticule at 2.4×. */
export const INK_DIM = '#6f8294'

/** Stale · 9.1:1. */
export const AMBER = '#e8a33d'

/** Warning · 7.1:1. Not a state: severity belongs to the alert bar, never to a marker. */
export const CRITICAL = '#ff6b6b'

/** The graticule's minor line · 1.76:1. Structure, never data. */
export const RULE = '#2c3d4e'

/** The graticule's major line · 2.58:1. */
export const RULE_LIT = '#3f566d'

/**
 * Every colour above, by the custom-property name `index.css` publishes it under.
 *
 * Exported for the test that reads the stylesheet back and compares. A record rather than the
 * stylesheet being generated from it: a build step that writes CSS would be a second thing to run
 * before the page is correct, and this is nine values.
 */
export const PALETTE: Readonly<Record<string, string>> = {
  '--map': MAP_BACKGROUND,
  '--panel': PANEL_SURFACE,
  '--ink-bright': INK_BRIGHT,
  '--ink': INK,
  '--ink-dim': INK_DIM,
  '--amber': AMBER,
  '--critical': CRITICAL,
  '--rule': RULE,
  '--rule-lit': RULE_LIT,
}
