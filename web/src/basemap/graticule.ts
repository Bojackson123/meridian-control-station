import type { Feature, FeatureCollection, LineString, Position } from 'geojson'
import type { GeoJSONSource, Map as MapLibreMap } from 'maplibre-gl'

/**
 * The console's basemap carries no terrain and no coastline, so the graticule is the only thing on
 * screen that says how far apart two points are. It is generated here rather than shipped as a data
 * file because a fixed-spacing grid is useless at both ends of the range the console needs: degree
 * lines are 111 km apart, which puts nothing at all on screen at the few-hundred-metre scale the
 * station operates at, while a grid fine enough for that scale is hundreds of thousands of lines
 * worldwide. Spacing is chosen from the viewport instead, which is one more argument to a small
 * function and no bytes in the repository.
 *
 * The grid is a reference, never data. Nothing here may read as a track or a vehicle -- a basemap
 * element mistaken for traffic is HAZ-01 wearing a different hat.
 */

/** Degree spacings the graticule is allowed to draw at, coarsest first. */
//  A 1-2-5 sequence, so no rung is more than 2.5x its neighbour and the division count stays inside
//  [4, 10) -- except the 3x step at the top, which reaches 12 when the whole globe is in view and is
//  not a scale this console operates at. The ratio between rungs is the whole design: leave a 5x gap
//  anywhere in the ladder and a viewport that falls just short of four cells at one spacing lands on
//  twenty at the next, which is how this was found.
const SPACING_LADDER_DEGREES = [
  30, 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005, 0.002, 0.001, 0.0005, 0.0002, 0.0001,
] as const

/** How many cells must fit across the viewport before a spacing is considered fine enough. */
const MINIMUM_DIVISIONS = 4

/** Every nth line is drawn as a major one, giving the grid a readable hierarchy. */
const MAJOR_LINE_INTERVAL = 5

/** The latitude Web Mercator stops at. Lines beyond it project to infinity. */
const MERCATOR_MAX_LATITUDE = 85.051129

/**
 * A ceiling on generated lines, so a degenerate viewport cannot hand MapLibre a runaway
 * FeatureCollection. Reaching it means the spacing choice is wrong, not that the grid should be
 * silently trimmed -- hence the console warning.
 */
const MAX_FEATURES = 512

/** The geographic extent a grid is generated for. */
export interface GraticuleBounds {
  west: number
  south: number
  east: number
  north: number
}

/**
 * A graticule expressed entirely in whole multiples of its spacing.
 *
 * The indices, not the degrees, are the primitive: a line's coordinate is always `index * spacing`,
 * computed fresh, so lines stay on round coordinates instead of drifting off them the way an
 * accumulated sum does at 0.0001 degrees. It also makes the grid comparable -- two grids with equal
 * indices are the same geometry, which is what lets a pan skip the rebuild.
 */
export interface GraticuleGrid {
  spacingDegrees: number
  westIndex: number
  eastIndex: number
  southIndex: number
  northIndex: number
}

/** Properties carried on every graticule line; the style filters its two layers on `major`. */
interface GraticuleLineProperties {
  major: boolean
}

/**
 * Picks the coarsest spacing that still puts {@link MINIMUM_DIVISIONS} cells across the viewport.
 *
 * Coarsest rather than finest on purpose: the grid should be the least dense thing that still
 * answers "how far is that", and erring dense turns a reference into visual noise the vehicle layer
 * has to compete with.
 */
export function chooseSpacingDegrees(longitudeSpanDegrees: number): number {
  const span = Math.abs(longitudeSpanDegrees)
  for (const spacing of SPACING_LADDER_DEGREES) {
    if (span / spacing >= MINIMUM_DIVISIONS) return spacing
  }

  //  Zoomed in past the finest rung. Drawing the finest grid we have beats drawing nothing.
  return SPACING_LADDER_DEGREES[SPACING_LADDER_DEGREES.length - 1]
}

/**
 * Snaps a viewport to the grid that covers it, padded by one cell on every side.
 *
 * The padding is not cosmetic. It is what allows {@link gridKey} to stand in for the whole geometry:
 * because the drawn extent is snapped outward to cell boundaries rather than cut at the viewport
 * edge, panning within a cell produces an identical grid and the source is left alone.
 */
export function gridFor(bounds: GraticuleBounds): GraticuleGrid {
  //  A viewport straddling the antimeridian reports an east smaller than its west. Unwrapping it
  //  rather than splitting the grid in two keeps the indices monotonic; MapLibre renders the
  //  out-of-range longitudes at the position they name.
  const west = bounds.west
  const east = bounds.east < bounds.west ? bounds.east + 360 : bounds.east

  const spacingDegrees = chooseSpacingDegrees(east - west)

  //  Indices are left unclamped. Mercator's limit is applied in graticuleFor, where the two axes can
  //  be treated differently: it bounds how far a meridian is drawn, but it removes parallels
  //  outright, and collapsing that into one pair of indices here is what left the top of a
  //  world-scale view empty.
  return {
    spacingDegrees,
    westIndex: Math.floor(west / spacingDegrees) - 1,
    eastIndex: Math.ceil(east / spacingDegrees) + 1,
    southIndex: Math.floor(bounds.south / spacingDegrees) - 1,
    northIndex: Math.ceil(bounds.north / spacingDegrees) + 1,
  }
}

/** A value that changes exactly when {@link graticuleFor} would produce different geometry. */
export function gridKey(grid: GraticuleGrid): string {
  return `${grid.spacingDegrees}/${grid.westIndex}:${grid.eastIndex}/${grid.southIndex}:${grid.northIndex}`
}

/**
 * Builds the grid's lines.
 *
 * Each line is two vertices. That is exact rather than an approximation: meridians and parallels are
 * both straight in Web Mercator, so there is nothing between the endpoints to describe. It stops
 * being true under a globe projection, which would need the segments densified.
 */
export function graticuleFor(grid: GraticuleGrid): FeatureCollection<LineString, GraticuleLineProperties> {
  const { spacingDegrees, westIndex, eastIndex, southIndex, northIndex } = grid
  const features: Feature<LineString, GraticuleLineProperties>[] = []

  //  Lines are drawn to the grid's own edges, not the viewport's, so every one ends on a cell
  //  boundary and the geometry stays a pure function of the spacing and the four indices.
  const west = westIndex * spacingDegrees
  const east = eastIndex * spacingDegrees

  //  A meridian is drawn to wherever Mercator stops, which is not generally a whole multiple of the
  //  spacing -- at 30 degree spacing the last parallel is 60, and cutting the meridians there too
  //  leaves a quarter of a world-scale screen with nothing on it.
  const south = Math.max(southIndex * spacingDegrees, -MERCATOR_MAX_LATITUDE)
  const north = Math.min(northIndex * spacingDegrees, MERCATOR_MAX_LATITUDE)

  for (let index = westIndex; index <= eastIndex; index++) {
    const longitude = index * spacingDegrees
    features.push(line([[longitude, south], [longitude, north]], index))
  }

  //  Parallels, by contrast, exist only where a whole multiple of the spacing does, so the ones past
  //  the projection's limit are dropped rather than moved to it.
  const firstParallel = Math.max(southIndex, Math.ceil(-MERCATOR_MAX_LATITUDE / spacingDegrees))
  const lastParallel = Math.min(northIndex, Math.floor(MERCATOR_MAX_LATITUDE / spacingDegrees))

  for (let index = firstParallel; index <= lastParallel; index++) {
    const latitude = index * spacingDegrees
    features.push(line([[west, latitude], [east, latitude]], index))
  }

  if (features.length > MAX_FEATURES) {
    console.warn(`Graticule generated ${features.length} lines at ${spacingDegrees}deg spacing; drawing the first ${MAX_FEATURES}.`)
    features.length = MAX_FEATURES
  }

  return { type: 'FeatureCollection', features }
}

function line(coordinates: Position[], index: number): Feature<LineString, GraticuleLineProperties> {
  return {
    type: 'Feature',
    geometry: { type: 'LineString', coordinates },
    //  Modulo of a negative index is negative or minus zero, both of which compare equal to zero,
    //  so the major lines stay aligned across the antimeridian and the equator.
    properties: { major: index % MAJOR_LINE_INTERVAL === 0 },
  }
}

/**
 * Keeps the style's `graticule` source in step with the viewport.
 *
 * Must be called after the style has loaded, since the source it writes to is declared there.
 *
 * Listens on `moveend`, not `move`. The memo makes a per-frame update look cheap on paper, but a
 * zoom animation grows the viewport every frame, so the grid genuinely differs every frame and each
 * one costs a re-index of the source in the worker. Tried it: the tiles never finish and the grid
 * stays visibly half-drawn for as long as the camera is moving. Waiting for the settle costs one
 * animation's worth of a stale grid instead, which is the better of the two.
 *
 * `resize` is subscribed for the case `moveend` cannot see: growing the window widens the visible
 * extent without moving the camera, so no move event fires and the grid would keep the extent of the
 * smaller canvas, leaving the new edges of the screen bare.
 */
export function attachGraticule(map: MapLibreMap, sourceId = 'graticule'): void {
  let lastKey = ''

  const update = () => {
    const source = map.getSource(sourceId) as GeoJSONSource | undefined
    if (!source) return

    const bounds = map.getBounds()
    const grid = gridFor({
      west: bounds.getWest(),
      south: bounds.getSouth(),
      east: bounds.getEast(),
      north: bounds.getNorth(),
    })

    const key = gridKey(grid)
    if (key === lastKey) return
    lastKey = key

    source.setData(graticuleFor(grid))
  }

  map.on('moveend', update)
  map.on('resize', update)
  update()
}
