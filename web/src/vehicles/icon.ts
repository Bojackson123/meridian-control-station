/**
 * The vehicle marker's artwork, drawn at runtime.
 *
 * Drawn rather than loaded, because the alternative is a sprite sheet, and the basemap style must
 * never grow a `sprite` key for the same reason it has no `glyphs` one: both are URLs MapLibre
 * fetches on its own, and the console is required to reach no origin but its own. A shape this
 * simple costs less as thirty lines of canvas than as a PNG plus the JSON index that locates it.
 *
 * It is a placeholder for designed symbology, and deliberately says nothing about state -- no colour
 * carries meaning here yet, so nothing has to be unlearned when a state language arrives.
 */

/** Drawn at twice the size it renders at, and registered with a matching `pixelRatio`. */
export const VEHICLE_ICON_PIXEL_RATIO = 2

/** The marker's rendered size in CSS pixels. Big enough to read a heading off, small enough not to hide the track. */
const ICON_SIZE_PIXELS = 24

/**
 * Near-white. The graticule sits between 1.76:1 and 2.58:1 against the background precisely so the
 * vehicle layer can be the brightest thing on screen; this is around 17:1.
 */
const FILL_STYLE = '#eef3f8'

/** The background colour, as an outline. It is what keeps the marker legible where it crosses a grid line. */
const OUTLINE_STYLE = '#080c12'

const OUTLINE_WIDTH_PIXELS = 1.5

/**
 * A dart, in CSS pixels within a {@link ICON_SIZE_PIXELS} box.
 *
 * **The nose points north**, which is what `icon-rotate` expects zero to mean. Artwork drawn any
 * other way puts every heading on screen off by a constant, and a constant offset reads as a
 * plausible track rather than as a bug.
 */
const OUTLINE: readonly (readonly [number, number])[] = [
  [12, 1.5],   // nose
  [20, 21],    // starboard tip
  [12, 16.5],  // tail notch, so the dart reads as pointed rather than as a triangle
  [4, 21],     // port tip
]

/**
 * Builds the marker bitmap for `map.addImage`.
 *
 * `ImageData` rather than an `HTMLImageElement` or a data URI: MapLibre accepts raw pixels directly,
 * so there is no decode to await and no image load that can fail after the layer already references
 * the icon.
 *
 * @throws If a 2D canvas context is unavailable, which means the marker would silently never appear.
 */
export function createVehicleIcon(): ImageData {
  const scale = VEHICLE_ICON_PIXEL_RATIO
  const extent = ICON_SIZE_PIXELS * scale

  const canvas = document.createElement('canvas')
  canvas.width = extent
  canvas.height = extent

  const context = canvas.getContext('2d')
  if (!context) {
    throw new Error('A 2D canvas context is required to draw the vehicle marker.')
  }

  context.scale(scale, scale)

  context.beginPath()
  for (const [x, y] of OUTLINE) context.lineTo(x, y)
  context.closePath()

  //  Stroked first, then filled over it, so the outline sits entirely outside the shape: a stroke
  //  drawn last would eat half its width off the dart's already narrow tips.
  context.lineJoin = 'round'
  context.lineWidth = OUTLINE_WIDTH_PIXELS
  context.strokeStyle = OUTLINE_STYLE
  context.stroke()

  context.fillStyle = FILL_STYLE
  context.fill()

  return context.getImageData(0, 0, extent, extent)
}
