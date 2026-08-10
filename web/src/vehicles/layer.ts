import type { Feature, FeatureCollection, Point } from 'geojson'
import type { GeoJSONSource, Map as MapLibreMap } from 'maplibre-gl'

import type { Fleet } from '../telemetry/types'
import { VEHICLE_ICON_PIXEL_RATIO, createVehicleIcon } from './icon'

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

/** What each feature carries. Only what a layer expression reads -- the frame itself stays with the client. */
interface VehicleFeatureProperties {
  vehicleId: string
  headingDegrees: number
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

  map.addSource(SOURCE_ID, { type: 'geojson', data: featuresFor(new Map()) })

  map.addLayer({
    id: LAYER_ID,
    type: 'symbol',
    source: SOURCE_ID,
    layout: {
      'icon-image': ICON_ID,

      //  Clockwise from north, the same convention the frame's heading uses, so this is a read
      //  rather than a conversion.
      'icon-rotate': ['get', 'headingDegrees'],

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
      properties: {
        vehicleId: frame.vehicleId,
        headingDegrees: frame.headingDegrees,
      },
    })
  }

  return { type: 'FeatureCollection', features }
}
