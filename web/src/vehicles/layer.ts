import type { Feature, FeatureCollection, Point } from 'geojson'
import type { GeoJSONSource, Map as MapLibreMap } from 'maplibre-gl'

import type { Fleet } from '../telemetry/types'
import {
  VEHICLE_ICON_PIXEL_RATIO,
  createHeadinglessVehicleIcon,
  createVehicleIcon,
} from './icon'

/**
 * The layer the fleet is drawn on.
 *
 * Source and layer are created here rather than declared in the basemap style, which the graticule's
 * precedent would suggest. The style file is a basemap: it ships no geodata, reaches no other origin,
 * and its graticule is orientation, which is a basemap's job. Vehicles are operational data, and a
 * style describing live data it can never contain would make that file mean two things. Keeping the
 * layer here also puts the icon, the paint expressions and the update in one module, which is where
 * a state language wants to live once there is one.
 *
 * A GeoJSON source and one symbol layer, not a DOM marker per vehicle. Markers are quicker to reach
 * a first dot and become a rewrite immediately afterwards: twelve absolutely-positioned nodes
 * fighting the map's transform, with rotation and per-state styling done by hand instead of by
 * data-driven expressions the style already evaluates.
 */

const SOURCE_ID = 'vehicles'
const LAYER_ID = 'vehicles'
const ICON_ID = 'vehicle-marker'
const HEADINGLESS_ICON_ID = 'vehicle-marker-headingless'

/** What each feature carries. Only what a layer expression reads -- the frame itself stays with the client. */
interface VehicleFeatureProperties {
  vehicleId: string

  //  Absent, not zero, when the vehicle did not report a heading. The property is omitted entirely
  //  rather than set to null, because that is what makes `has` a reliable test in the expressions
  //  below -- a property present and null would answer true and select the dart.
  headingDegrees?: number
}

/**
 * Adds the vehicle layer to a loaded map and returns the function that keeps it current.
 *
 * Must be called after the style has loaded, since it adds to it.
 *
 * @returns An updater. Call it with the whole fleet; it replaces the layer's contents outright.
 */
export function attachVehicleLayer(map: MapLibreMap): (fleet: Fleet) => void {
  map.addImage(ICON_ID, createVehicleIcon(), { pixelRatio: VEHICLE_ICON_PIXEL_RATIO })
  map.addImage(HEADINGLESS_ICON_ID, createHeadinglessVehicleIcon(), {
    pixelRatio: VEHICLE_ICON_PIXEL_RATIO,
  })

  map.addSource(SOURCE_ID, { type: 'geojson', data: featuresFor(new Map()) })

  map.addLayer({
    id: LAYER_ID,
    type: 'symbol',
    source: SOURCE_ID,
    layout: {
      //  A vehicle that reported no heading loses its nose rather than being pointed somewhere.
      //  Zero would be north, and a marker asserting a direction nothing reported is the display
      //  claiming what it cannot support -- the same reason the state language drops the heading on
      //  a lost track. Shape says whether a direction is known; fill says whether the data is
      //  current; they are separate channels and this one is the first.
      'icon-image': ['case', ['has', 'headingDegrees'], ICON_ID, HEADINGLESS_ICON_ID],

      //  Clockwise from north, the same convention the frame's heading uses, so this is a read
      //  rather than a conversion. The fallback is only ever applied to the headingless marker,
      //  which is round and so rotates to no effect; it exists because the expression must return a
      //  number for every feature, not because zero means anything here.
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
    },
  })

  return (fleet: Fleet) => {
    //  Re-resolved on each update rather than captured: a style reload drops every source, and a
    //  stale handle would go on accepting data nothing renders.
    const source = map.getSource(SOURCE_ID) as GeoJSONSource | undefined
    if (!source) return

    source.setData(featuresFor(fleet))
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
 * purpose and called a feature. At the feed's rate the marker steps once a second, visibly, and that
 * is the correct behaviour.
 */
function featuresFor(fleet: Fleet): FeatureCollection<Point, VehicleFeatureProperties> {
  const features: Feature<Point, VehicleFeatureProperties>[] = []

  for (const frame of fleet.values()) {
    features.push({
      type: 'Feature',
      geometry: {
        type: 'Point',
        coordinates: [frame.longitudeDegrees, frame.latitudeDegrees],
      },
      //  Spread rather than assigned, so an unreported heading leaves the key off the feature
      //  entirely. Assigning null -- or undefined -- would put a property there for `has` to find,
      //  and the marker would take its nose back and point at the coalesce fallback of north.
      //
      //  Tested for being a number rather than against null, because a frame is cast from
      //  JSON.parse and never validated, so absence reaches here as undefined the moment anything
      //  upstream omits the key instead of nulling it -- a serialiser configured to skip nulls, or
      //  a second producer of this shape. `=== null` would miss that and draw the confident north.
      properties: {
        vehicleId: frame.vehicleId,
        ...(typeof frame.headingDegrees === 'number' ? { headingDegrees: frame.headingDegrees } : {}),
      },
    })
  }

  return { type: 'FeatureCollection', features }
}
