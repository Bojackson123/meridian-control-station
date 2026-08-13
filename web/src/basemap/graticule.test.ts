import { describe, expect, it } from 'vitest'

import { chooseSpacingDegrees, gridFor, gridKey, graticuleFor } from './graticule'

/**
 * The graticule's arithmetic.
 *
 * These four functions were written to be callable without a map precisely so that they could be
 * tested directly the day this project had somewhere to put a test, and this is that day. Nothing
 * here touches MapLibre: `attachGraticule` is the only part that needs one, and it is a subscription
 * around `graticuleFor` rather than logic of its own.
 *
 * The grid is a reference and never data. Nothing it draws may read as a track — which is why the
 * spacing ladder's job is to be the *least* dense grid that still answers "how far is that".
 */

describe('chooseSpacingDegrees', () => {
  //  The whole point of a ladder rather than a formula: no rung may be so far from its neighbour
  //  that a viewport falling just short of four cells at one spacing lands on twenty at the next.
  //  That is how the 1-2-5 sequence was arrived at, and this is the property it was arrived at for.
  it('keeps the division count in single digits at every scale the console operates at', () => {
    //  A decade below the 400 m circuit and a decade above the widest useful view.
    for (let span = 0.0005; span < 30; span *= 1.07) {
      const divisions = span / chooseSpacingDegrees(span)

      expect(divisions, `${span}deg span`).toBeGreaterThanOrEqual(4)
      expect(divisions, `${span}deg span`).toBeLessThan(10)
    }
  })

  it('takes the coarsest spacing that still divides the viewport, not the finest', () => {
    //  0.5deg divides into 4 cells exactly; going finer would draw ten where four answer the
    //  question, and a dense grid is noise the vehicle layer has to compete with.
    expect(chooseSpacingDegrees(2)).toBe(0.5)
  })

  it('reads a span backwards the same as forwards', () => {
    expect(chooseSpacingDegrees(-2)).toBe(chooseSpacingDegrees(2))
  })

  it('draws the finest grid it has rather than nothing, zoomed in past the ladder', () => {
    expect(chooseSpacingDegrees(1e-9)).toBe(0.0001)
  })
})

describe('gridFor', () => {
  //  The padding is not cosmetic: it is what lets gridKey stand in for the whole geometry, because
  //  a pan within a cell then produces an identical grid and the source is left alone.
  it('snaps outward to whole cells and pads by one on every side', () => {
    const grid = gridFor({ west: -86.59, south: 34.72, east: -86.58, north: 34.74 })

    expect(grid.westIndex * grid.spacingDegrees).toBeLessThan(-86.59)
    expect(grid.eastIndex * grid.spacingDegrees).toBeGreaterThan(-86.58)
    expect(grid.southIndex * grid.spacingDegrees).toBeLessThan(34.72)
    expect(grid.northIndex * grid.spacingDegrees).toBeGreaterThan(34.74)
  })

  it('gives a pan within one cell the same key, and a pan across one a different key', () => {
    const at = (west: number) =>
      gridKey(gridFor({ west, south: 34.72, east: west + 0.01, north: 34.74 }))

    //  The spacing at this span is 0.002deg. Both viewports below sit inside the same cell -- and
    //  deliberately not on its edge, where the snapping is what is under test rather than the
    //  padding -- while the third has moved on by a whole cell.
    expect(at(-86.5891)).toBe(at(-86.589))
    expect(at(-86.5891)).not.toBe(at(-86.5871))
  })

  //  A viewport straddling the antimeridian reports an east smaller than its west. Unwrapping keeps
  //  the indices monotonic rather than splitting the grid in two.
  it('unwraps a viewport that crosses the antimeridian', () => {
    const grid = gridFor({ west: 179, south: -10, east: -179, north: 10 })

    expect(grid.eastIndex).toBeGreaterThan(grid.westIndex)
    expect(grid.eastIndex * grid.spacingDegrees).toBeGreaterThan(180)
  })
})

describe('graticuleFor', () => {
  it('draws every line on a whole multiple of the spacing', () => {
    const grid = gridFor({ west: -86.59, south: 34.72, east: -86.58, north: 34.74 })

    for (const feature of graticuleFor(grid).features) {
      for (const [longitude, latitude] of feature.geometry.coordinates) {
        //  A meridian's ends are clamped to Mercator's limit rather than to a cell boundary, so
        //  only the constant axis of each line is checked against the grid.
        const along = feature.geometry.coordinates[0][0] === longitude ? longitude : latitude

        expect(Math.abs(along / grid.spacingDegrees % 1)).toBeLessThan(1e-6)
      }
    }
  })

  //  Mercator's limit removes a parallel outright but only shortens a meridian. Collapsing the two
  //  into one pair of indices is what once left the top of a world-scale view empty.
  it('stops at the projection\'s limit without leaving the top of the world bare', () => {
    const features = graticuleFor(gridFor({ west: -180, south: -85, east: 180, north: 85 })).features

    const latitudes = features.flatMap((feature) =>
      feature.geometry.coordinates.map(([, latitude]) => latitude),
    )

    expect(Math.max(...latitudes)).toBeGreaterThan(80)
    expect(Math.max(...latitudes)).toBeLessThanOrEqual(85.051129)
    expect(Math.min(...latitudes)).toBeGreaterThanOrEqual(-85.051129)
  })

  it('marks every fifth line major, aligned across the equator', () => {
    const grid = gridFor({ west: -1, south: -1, east: 1, north: 1 })
    const features = graticuleFor(grid)

    //  Zero is a multiple of five however the sign of the modulo falls out, so the equator and the
    //  prime meridian are both major and the hierarchy does not flip across either.
    const equator = features.features.find(
      (feature) => feature.geometry.coordinates.every(([, latitude]) => latitude === 0),
    )

    expect(equator?.properties.major).toBe(true)
  })
})
