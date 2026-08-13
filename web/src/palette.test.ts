import { describe, expect, it } from 'vitest'

import indexCss from './index.css?raw'
import { PALETTE } from './palette'

/**
 * The stylesheet and the drawing code agree about what the colours are.
 *
 * They have to be stated twice — a canvas takes a string and cannot import a stylesheet — so the
 * drift is guarded rather than prevented. It is worth guarding: a stale marker drawn in one amber
 * on the map and labelled in another in the panel is the design note's opening complaint, three
 * different greys meaning three different things, arriving one honest edit at a time.
 *
 * The stylesheet is read as text rather than through a live document, because what is being checked
 * is the file that ships — a `getComputedStyle` reading would agree with itself in a browser that
 * had already resolved a variable this test never saw declared.
 */

/** Pulls `--name: #value;` declarations out of the `:root` block. */
const declaredInRoot = (css: string): Map<string, string> => {
  //  Comments first. A comment that mentions a custom property by name -- and the ones in
  //  index.css do, because that is how they explain themselves -- otherwise parses as a
  //  declaration whose value runs to the next semicolon several lines below.
  const root = /:root\s*\{([^}]*)\}/.exec(css.replaceAll(/\/\*[\s\S]*?\*\//g, ''))
  if (!root) throw new Error('index.css has no :root block to read the palette from.')

  const declarations = new Map<string, string>()

  for (const [, name, value] of root[1].matchAll(/(--[a-z-]+)\s*:\s*([^;]+);/g)) {
    declarations.set(name, value.trim())
  }

  return declarations
}

describe('the palette', () => {
  const declared = declaredInRoot(indexCss)

  for (const [name, value] of Object.entries(PALETTE)) {
    it(`declares ${name} in index.css as ${value}`, () => {
      expect(declared.get(name)).toBe(value)
    })
  }
})
