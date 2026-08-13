import { AMBER, INK_BRIGHT, INK_DIM, MAP_BACKGROUND } from '../palette'
import type { MarkerFill, MarkerShape, VehicleAppearance } from './appearance'
import {
  CENTRE,
  DART_POINTS,
  DISC_RADIUS,
  DRAWING_EXTENT,
  OUTLINE_WIDTH,
  RING_DASH,
  RING_GAP,
  RING_RADIUS,
} from './geometry'

/**
 * The vehicle markers' artwork, drawn at runtime.
 *
 * Drawn rather than loaded, because the alternative is a sprite sheet, and the basemap style must
 * never grow a `sprite` key for the same reason it has no `glyphs` one: both are URLs MapLibre
 * fetches on its own, and the console is required to reach no origin but its own. Five shapes this
 * simple cost less as a table and one canvas routine than as a PNG plus the JSON index that
 * locates it.
 *
 * **Every shape here comes from `geometry.ts`, scaled.** The design note draws them in a 60-unit
 * box; this draws them in a {@link ICON_SIZE_PIXELS} one, so everything below is the note's number
 * times {@link FROM_DRAWING}. Redrawing them by eye at this size is how a marker ends up meaning
 * something slightly different from the thing that was designed and inspected.
 */

/** Drawn at twice the size it renders at, and registered with a matching `pixelRatio`. */
export const VEHICLE_ICON_PIXEL_RATIO = 2

/** The marker's rendered size in CSS pixels. Big enough to read a heading off, small enough not to hide the track. */
const ICON_SIZE_PIXELS = 24

/**
 * The design note's drawing is in a 60-unit box and this one is 24 CSS pixels across.
 *
 * A marker's channels have to survive a 24-pixel box — that is the lesson the note records from
 * the return-to-launch badge, whose glyph came out about four pixels across and read as a smudge.
 * Fill, outline style and enclosure survive at this size; interior detail does not, which is why
 * nothing here is an icon.
 */
const FROM_DRAWING = ICON_SIZE_PIXELS / DRAWING_EXTENT

const DART_PIXELS: readonly (readonly [number, number])[] =
  DART_POINTS.map(([x, y]) => [x * FROM_DRAWING, y * FROM_DRAWING] as const)

const DISC_RADIUS_PIXELS = DISC_RADIUS * FROM_DRAWING

const RING_RADIUS_PIXELS = RING_RADIUS * FROM_DRAWING

/**
 * The background colour, drawn as a halo under a solid marker.
 *
 * It is what keeps the marker legible where it crosses a grid line. A hollow marker needs no halo:
 * its interior is already painted in the background colour, which does the same job from the
 * inside.
 */
const HALO_WIDTH_PIXELS = 1.5

/** The width of an outline that *is* the marker, rather than one separating it from the map. */
const OUTLINE_WIDTH_PIXELS = OUTLINE_WIDTH * FROM_DRAWING

const RING_DASH_PIXELS = RING_DASH * FROM_DRAWING

const RING_GAP_PIXELS = RING_GAP * FROM_DRAWING

/** How a marker is painted. */
interface MarkerInk {
  shape: MarkerShape

  /** The interior. Always painted: it is what stops the graticule showing through a marker. */
  fill: string

  /** The outline's colour. */
  outline: string

  outlineWidth: number

  /**
   * Whether the outline is a halo beneath the fill, or the marker itself drawn over it.
   *
   * The two are the same two values `MarkerFill` distinguishes, arriving at the canvas: a solid
   * marker is a body with a thin separation from the map behind it, and a hollow or dashed one is
   * an outline with the map masked out inside it.
   */
  outlineIsHalo: boolean

  dashed?: boolean
}

/**
 * The five markers the state language produces, by the id the map layer asks for them under.
 *
 * Five, not the nine that `MarkerShape` × `MarkerFill` would allow — `appearanceOf` only ever
 * produces these, and generating the rest would be drawing shapes that mean nothing to defend an
 * ordering nothing performs. `vehicleIconsAreDrawnForEveryState` in the tests is what keeps the
 * two in step.
 */
const MARKER_INKS: Readonly<Record<string, MarkerInk>> = {
  //  Live, and the brightest thing on the map. The graticule sits between 1.76:1 and 2.58:1
  //  against the background precisely so this can be around 17:1.
  'vehicle-dart-solid': {
    shape: 'dart',
    fill: INK_BRIGHT,
    outline: MAP_BACKGROUND,
    outlineWidth: HALO_WIDTH_PIXELS,
    outlineIsHalo: true,
  },

  //  Live, with no heading reported. The same ink and no nose: shape carries direction, fill
  //  carries currency, and a frame can be current without saying which way the vehicle faces.
  'vehicle-disc-solid': {
    shape: 'disc',
    fill: INK_BRIGHT,
    outline: MAP_BACKGROUND,
    outlineWidth: HALO_WIDTH_PIXELS,
    outlineIsHalo: true,
  },

  //  Stale: the same dart, hollowed. The heading is frozen where it stopped rather than dropped,
  //  because three seconds of silence is a gap and the last reported heading is still the best
  //  answer available.
  'vehicle-dart-hollow': {
    shape: 'dart',
    fill: MAP_BACKGROUND,
    outline: AMBER,
    outlineWidth: OUTLINE_WIDTH_PIXELS,
    outlineIsHalo: false,
  },

  'vehicle-disc-hollow': {
    shape: 'disc',
    fill: MAP_BACKGROUND,
    outline: AMBER,
    outlineWidth: OUTLINE_WIDTH_PIXELS,
    outlineIsHalo: false,
  },

  //  Lost, and the station's answer to a vehicle it has stopped hearing from: a ring, dashed, at
  //  the dimmest level in the ladder. It says a vehicle was here and declines to say anything
  //  further, which is the honest content of the data.
  'vehicle-ring-dashed': {
    shape: 'ring',
    fill: MAP_BACKGROUND,
    outline: INK_DIM,
    outlineWidth: OUTLINE_WIDTH_PIXELS,
    outlineIsHalo: false,
    dashed: true,
  },
}

/** Every marker id, for the layer to register and for the tests to check nothing is missing. */
export const VEHICLE_ICON_IDS: readonly string[] = Object.keys(MARKER_INKS)

/**
 * Which marker a vehicle's appearance selects.
 *
 * Shape and fill are the two channels the note gives the marker, so between them they name it. The
 * id is built rather than stored on the appearance, which keeps `appearance.ts` a statement about
 * the language and free of anything MapLibre needs to be told.
 */
export function vehicleIconId(appearance: Pick<VehicleAppearance, 'shape' | 'fill'>): string {
  return iconIdFor(appearance.shape, appearance.fill)
}

/** The id under which a shape and fill are registered. */
export function iconIdFor(shape: MarkerShape, fill: MarkerFill): string {
  return `vehicle-${shape}-${fill}`
}

/**
 * Draws every marker, for `map.addImage`.
 *
 * `ImageData` rather than an `HTMLImageElement` or a data URI: MapLibre accepts raw pixels
 * directly, so there is no decode to await and no image load that can fail after the layer already
 * references the icon.
 *
 * @throws If a 2D canvas context is unavailable, which means the markers would silently never appear.
 */
export function createVehicleIcons(): { id: string; image: ImageData }[] {
  return Object.entries(MARKER_INKS).map(([id, ink]) => ({ id, image: draw(ink) }))
}

/** The canvas scaffolding all five share, so they cannot drift apart in ink or scale. */
function draw(ink: MarkerInk): ImageData {
  const scale = VEHICLE_ICON_PIXEL_RATIO
  const extent = ICON_SIZE_PIXELS * scale

  const canvas = document.createElement('canvas')
  canvas.width = extent
  canvas.height = extent

  const context = canvas.getContext('2d')
  if (!context) {
    throw new Error('A 2D canvas context is required to draw the vehicle markers.')
  }

  context.scale(scale, scale)

  trace(context, ink.shape)

  context.lineJoin = 'round'
  context.lineWidth = ink.outlineWidth
  context.strokeStyle = ink.outline
  context.fillStyle = ink.fill

  //  Butt caps, the canvas default, and left alone deliberately. Round caps extend every dash by
  //  half a line width at each end, which at this size very nearly closes the gaps -- and the gaps
  //  are the channel. A ring that reads as solid at a glance is a lost vehicle wearing stale's
  //  outline.
  if (ink.dashed) context.setLineDash(dashFitting(RING_RADIUS_PIXELS))

  if (ink.outlineIsHalo) {
    //  Stroked first, then filled over it, so the halo sits entirely outside the shape: a stroke
    //  drawn last would eat half its width off the dart's already narrow tips.
    context.stroke()
    context.fill()

    return context.getImageData(0, 0, extent, extent)
  }

  //  Filled first, then stroked over it. Here the outline is the marker, and it is meant to sit
  //  astride the edge -- the fill is only there to mask the graticule out of the interior.
  context.fill()
  context.stroke()

  return context.getImageData(0, 0, extent, extent)
}

/** Lays the shape down as a path, without deciding how it is painted. */
function trace(context: CanvasRenderingContext2D, shape: MarkerShape): void {
  const centre = CENTRE * FROM_DRAWING

  context.beginPath()

  if (shape === 'dart') {
    for (const [x, y] of DART_PIXELS) context.lineTo(x, y)
    context.closePath()

    return
  }

  const radius = shape === 'ring' ? RING_RADIUS_PIXELS : DISC_RADIUS_PIXELS

  context.arc(centre, centre, radius, 0, 2 * Math.PI)
  context.closePath()
}

/**
 * A dash pattern in the note's proportions that closes exactly around a circle of this radius.
 *
 * The note draws the ring's dashes at 7 and 5.5 in its 60-unit box, which does not divide the
 * circumference at this size: the last gap comes out short and the ring reads as having a nick
 * taken out of it at three o'clock, in every marker, identically. Keeping the ratio and fitting a
 * whole number of periods costs one line and removes an artefact that looks like data.
 */
function dashFitting(radius: number): [number, number] {
  const designedPeriod = RING_DASH_PIXELS + RING_GAP_PIXELS
  const circumference = 2 * Math.PI * radius

  const periods = Math.max(1, Math.round(circumference / designedPeriod))
  const scale = circumference / (periods * designedPeriod)

  return [RING_DASH_PIXELS * scale, RING_GAP_PIXELS * scale]
}
