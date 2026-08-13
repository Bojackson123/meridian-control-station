import type { Feature, FeatureCollection, Point } from 'geojson'
import type { GeoJSONSource, Map as MapLibreMap } from 'maplibre-gl'

import type { StationLink } from '../telemetry/client'
import type { Fleet } from '../telemetry/types'
import { appearanceOf } from './appearance'
import { VEHICLE_ICON_PIXEL_RATIO, createVehicleIcons, vehicleIconId } from './icon'

/**
 * The layer the fleet is drawn on.
 *
 * Source and layer are created here rather than declared in the basemap style, which the graticule's
 * precedent would suggest. The style file is a basemap: it ships no geodata, reaches no other origin,
 * and its graticule is orientation, which is a basemap's job. Vehicles are operational data, and a
 * style describing live data it can never contain would make that file mean two things. Keeping the
 * layer here also puts the icons, the paint expressions and the update in one module.
 *
 * A GeoJSON source and one symbol layer, not a DOM marker per vehicle. Markers are quicker to reach
 * a first dot and become a rewrite immediately afterwards: twelve absolutely-positioned nodes
 * fighting the map's transform, with rotation and per-state styling done by hand instead of by
 * data-driven expressions the style already evaluates.
 *
 * **What state a vehicle is in is not decided here.** Every feature's icon, rotation and stacking
 * order comes from `appearanceOf`, which the fleet panel also calls. The two surfaces showing the
 * same vehicle differently -- one deriving from the state, the other from the age -- is exactly
 * what MCS-003 is written to prevent.
 */

const SOURCE_ID = 'vehicles'
const LAYER_ID = 'vehicles'

/** What each feature carries. Only what a layer expression reads -- the frame itself stays with the client. */
interface VehicleFeatureProperties {
  vehicleId: string

  /** Which of the five markers to draw. Resolved from the appearance, never from the frame. */
  iconId: string

  /** Live above stale above lost, from the appearance's `sortKey`. */
  sortKey: number

  //  Absent, not zero, when the marker declines to point -- a vehicle that reported no heading, or
  //  one the station has lost. The property is omitted entirely rather than set to null, because
  //  that is what makes `has` a reliable test in the expressions below: a property present and null
  //  would answer true and rotate the marker to the fallback.
  headingDegrees?: number
}

/**
 * Adds the vehicle layer to a loaded map and returns the function that keeps it current.
 *
 * Must be called after the style has loaded, since it adds to it.
 *
 * @returns An updater. Call it with the whole snapshot; it replaces the layer's contents outright.
 */
export function attachVehicleLayer(
  map: MapLibreMap,
): (fleet: Fleet, station: StationLink) => void {
  for (const { id, image } of createVehicleIcons()) {
    map.addImage(id, image, { pixelRatio: VEHICLE_ICON_PIXEL_RATIO })
  }

  map.addSource(SOURCE_ID, { type: 'geojson', data: featuresFor(new Map(), 'connected') })

  map.addLayer({
    id: LAYER_ID,
    type: 'symbol',
    source: SOURCE_ID,
    layout: {
      'icon-image': ['get', 'iconId'],

      //  Clockwise from north, the same convention the frame's heading uses, so this is a read
      //  rather than a conversion. The fallback is only ever applied to the round markers, which
      //  rotate to no effect; it exists because the expression must return a number for every
      //  feature, not because zero means anything here.
      'icon-rotate': ['coalesce', ['get', 'headingDegrees'], 0],

      //  Rotate with the map, not with the screen. The two agree only while the bearing is zero,
      //  and the day the console gains a track-up view is not the day to discover that.
      'icon-rotation-alignment': 'map',

      //  Both flags turn MapLibre's label collision handling off for this layer, and neither is
      //  cosmetic. Left on, two vehicles close enough to overlap are resolved by *hiding* one --
      //  a console showing fewer aircraft than are flying, which is HAZ-01 with a rendering
      //  optimisation as its cause. An overlapping pair of markers is honest; a missing one is not.
      'icon-allow-overlap': true,
      'icon-ignore-placement': true,

      //  With overlap allowed, MapLibre draws the higher sort key over the lower -- which is what
      //  the note means by z-ordering the markers by confidence. Clustering was rejected outright
      //  (it hides the thing you need at the density where you need it), so overlap is ordinary and
      //  the only question is which of the two is legible. It is always the one whose position the
      //  station still believes.
      'symbol-sort-key': ['get', 'sortKey'],
    },
  })

  return (fleet: Fleet, station: StationLink) => {
    //  Re-resolved on each update rather than captured: a style reload drops every source, and a
    //  stale handle would go on accepting data nothing renders.
    const source = map.getSource(SOURCE_ID) as GeoJSONSource | undefined
    if (!source) return

    source.setData(featuresFor(fleet, station))
  }
}

/**
 * Projects the fleet onto the source's contents.
 *
 * The whole collection is replaced every update rather than diffed. At the store's ceiling of twelve
 * vehicles the collection is smaller than the bookkeeping a diff would need, and a full replacement
 * cannot leave a vehicle on screen that is no longer in the fleet.
 *
 * Positions are used exactly as reported, with no interpolation between frames and no animation
 * toward the next one. Smoothing the motion would put the vehicle at a position it never reported,
 * which is HAZ-01 -- a picture the operator believes is current, and is not -- implemented on
 * purpose and called a feature. At the rate the aircraft reports its position the marker steps
 * four times a second, visibly, and that is the correct behaviour. A slower link steps more
 * coarsely and should: the stepping is the link's rate made visible, not a rendering artefact to
 * be smoothed away.
 *
 * A vehicle stays on the map at its last position through stale and lost alike, and that is the
 * point of the language rather than an oversight -- the last known position is information, and the
 * marker is what says how old it is. Removing it would answer "where was it?" with nothing.
 */
function featuresFor(
  fleet: Fleet,
  station: StationLink,
): FeatureCollection<Point, VehicleFeatureProperties> {
  const features: Feature<Point, VehicleFeatureProperties>[] = []

  for (const frame of fleet.values()) {
    const appearance = appearanceOf(frame, station)

    features.push({
      type: 'Feature',
      geometry: {
        type: 'Point',
        coordinates: [frame.longitudeDegrees, frame.latitudeDegrees],
      },
      //  Spread rather than assigned, so a marker that declines to point leaves the key off the
      //  feature entirely. Assigning null -- or undefined -- would put a property there for `has`
      //  to find, and the marker would take its nose back and point at the coalesce fallback of
      //  north.
      //
      //  Which is why the test is `typeof`, not `=== null`: spreading an object whose one key holds
      //  `undefined` still puts that key on the result, and `undefined` is exactly what an omitted
      //  heading is by the time it reaches here, a frame being unvalidated `JSON.parse` output.
      //  Against `=== null` the comment above would be false for the commonest form of absence.
      properties: {
        vehicleId: frame.vehicleId,
        iconId: vehicleIconId(appearance),
        sortKey: appearance.sortKey,
        ...(typeof appearance.headingDegrees === 'number'
          ? { headingDegrees: appearance.headingDegrees }
          : {}),
      },
    })
  }

  return { type: 'FeatureCollection', features }
}
