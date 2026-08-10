import { useEffect, useRef } from 'react'
import { MapLibreMap, ScaleControl } from 'maplibre-gl'

//  MapLibre's controls render as unstyled text without this. It is bundled, not linked, which is
//  the same reason as everything else here: the console must not reach off-origin for anything.
import 'maplibre-gl/dist/maplibre-gl.css'

import { attachGraticule } from './basemap/graticule'
import { configureMapLibreWorker } from './basemap/worker'
import { connectTelemetry } from './telemetry/client'
import { attachVehicleLayer } from './vehicles/layer'
import './App.css'

/**
 * The console's map shell: a full-bleed MapLibre map, showing the fleet on a basemap served entirely
 * from this origin.
 *
 * The map is the whole page, and that is a decision rather than a stage of construction. State
 * language, vehicle lists and alert surfacing are a designed set that has to be designed once and
 * used everywhere, so the page holds a marker and nothing else until that design exists -- anything
 * added here in the meantime would be built twice, and the second time would have to argue with the
 * first.
 */
function App() {
  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibreMap | null>(null)

  useEffect(() => {
    //  React's StrictMode runs this effect twice in development. The cleanup below makes that safe
    //  on its own; the guard covers the other case, a re-run that never got one, which otherwise
    //  stacks a second map instance on the container and looks like a rendering bug rather than a
    //  lifecycle one.
    if (mapRef.current) return

    configureMapLibreWorker()

    const map = new MapLibreMap({
      container: containerRef.current!,
      style: '/basemap/style.json',

      //  No center or zoom: MapLibre takes them from the style on load when the map was built
      //  without them, so the default view lives in the basemap instead of being a second copy of
      //  coordinates that already exist elsewhere.
    })
    mapRef.current = map

    //  The scale bar is not decoration here. With no terrain and no labels on the basemap, it and
    //  the graticule are the only distance references an operator has.
    map.addControl(new ScaleControl({ unit: 'metric' }), 'bottom-left')

    //  Torn down before the map has loaded, this effect still has to undo a connection that the load
    //  handler may be about to open.
    let disconnect: (() => void) | null = null
    let cancelled = false

    map.on('load', () => {
      attachGraticule(map)
      const setFleet = attachVehicleLayer(map)

      if (cancelled) return

      //  **The connection waits for the map.** Opening it alongside the map instead -- so the
      //  snapshot and the style parse in parallel -- costs the basemap entirely: MapLibre loads its
      //  worker from a script request the browser schedules at low priority, and an SSE stream is a
      //  response that never completes, so the scheduler leaves that request queued behind it. The
      //  symptom is not subtle and is not obviously about connections: the background paints, no data
      //  layer ever appears, and `load` never fires, indefinitely. Measured on this basemap at
      //  45 seconds and still waiting, against six with the connection opened here.
      disconnect = connectTelemetry(setFleet)
    })

    return () => {
      cancelled = true
      disconnect?.()
      map.remove()
      mapRef.current = null
    }
  }, [])

  return <div ref={containerRef} className="map" />
}

export default App
