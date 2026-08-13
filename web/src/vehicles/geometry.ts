/**
 * The marker shapes, in the design note's own coordinates.
 *
 * `docs/notes/console-design/mockup.html` draws every marker in a 60-unit box, and two things in
 * this console draw them for real: the canvas icons the map layer registers, and the inline SVG
 * badge at the head of each panel row. They are different rendering technologies drawing the same
 * language, and if each carried its own copy of the geometry they would drift — a dart with a
 * slightly different tail in the panel from the one on the map is the state language quietly
 * becoming two.
 *
 * So the numbers live here once, unscaled, and each renderer scales them into its own box.
 */

/** The side of the box every coordinate below is expressed in. */
export const DRAWING_EXTENT = 60

/** The centre of that box, which every round shape is drawn about. */
export const CENTRE = DRAWING_EXTENT / 2

/**
 * The dart.
 *
 * **The nose points north**, which is what MapLibre's `icon-rotate` expects zero to mean. Artwork
 * drawn any other way puts every heading on screen off by a constant, and a constant offset reads
 * as a plausible track rather than as a bug.
 */
export const DART_POINTS: readonly (readonly [number, number])[] = [
  [30, 3.75],   // nose
  [50, 52.5],   // starboard tip
  [30, 41.25],  // tail notch, so the dart reads as pointed rather than as a triangle
  [10, 52.5],   // port tip
]

/** The same dart as an SVG path, built from the points rather than written out beside them. */
export const DART_PATH =
  `M ${DART_POINTS.map(([x, y]) => `${x},${y}`).join(' L ')} Z`

/** The headingless marker's radius, sized to cover about the same ink as the dart. */
export const DISC_RADIUS = 17.5

/** The lost marker's radius. Wider than the disc: it is an enclosure rather than a body. */
export const RING_RADIUS = 21

/** The width of an outline that *is* the marker, rather than one separating it from the map. */
export const OUTLINE_WIDTH = 4

/**
 * The same, for the panel's badge.
 *
 * Heavier, because the badge renders in eighteen pixels where the map's marker gets twenty-six. A
 * single width scaled to both leaves the badge's hollow dart as a hairline, and a channel that
 * survives at one size and not the other is not a channel.
 */
export const BADGE_OUTLINE_WIDTH = 5

/** The dash the lost ring is drawn with. */
export const RING_DASH = 7

/** The gap between them. Wide enough that the dashing is the channel it is meant to be. */
export const RING_GAP = 5.5
