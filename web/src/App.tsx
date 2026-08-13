import { useEffect, useRef, useState } from 'react'
import { MapLibreMap, ScaleControl } from 'maplibre-gl'

//  MapLibre's controls render as unstyled text without this. It is bundled, not linked, which is
//  the same reason as everything else here: the console must not reach off-origin for anything.
import 'maplibre-gl/dist/maplibre-gl.css'

import { attachGraticule } from './basemap/graticule'
import { configureMapLibreWorker } from './basemap/worker'
import { coalesceToFrames } from './coalesce'
import { FleetPanel } from './panel/FleetPanel'
import { StationBar } from './panel/StationBar'
import type { ConsoleSnapshot } from './telemetry/client'
import { connectTelemetry } from './telemetry/client'
import { attachVehicleChips } from './vehicles/chips'
import { attachVehicleLayer } from './vehicles/layer'
import './App.css'

/**
 * The console: a station bar across the top, the fleet on a map, and the fleet listed beside it.
 *
 * The three regions are the design note's layout, at the note's arithmetic. The bar is outside both
 * of the others structurally, so nothing an operator can do — pan, zoom, scroll — can put it off
 * screen; the panel is sized for twelve rows without a scrollbar; and the map takes what is left.
 *
 * **Two surfaces, one snapshot.** The map is updated imperatively and the panel through React
 * state, but both are handed the same `ConsoleSnapshot` in the same animation frame, and both turn
 * it into a rendering through the same `appearanceOf`. That is what makes the marker and the row
 * agree — and disagreeing at the boundary, one surface amber while the other is not, is a small
 * HAZ-01 of its own (MCS-003).
 *
 * React does not own the map, and deliberately. Feeding MapLibre through a component tree means
 * re-rendering to hand it data it copies into its own buffers anyway; the map is created once,
 * imperatively, and the effect below is the whole of the bridge.
 */
function App() {
  const containerRef = useRef<HTMLDivElement>(null)
  const mapRef = useRef<MapLibreMap | null>(null)

  //  Optimistic in the same way the client is, and for the same reason: there is nothing to be
  //  wrong about while the fleet is empty, and a console that paints STATION UNREACHABLE across
  //  every page load teaches the operator to read the bar as decoration.
  const [snapshot, setSnapshot] = useState<ConsoleSnapshot>({
    fleet: new Map(),
    station: 'connected',
  })

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
    let detachChips: (() => void) | null = null
    let cancelPending: (() => void) | null = null
    let cancelled = false

    map.on('load', () => {
      attachGraticule(map)
      const setFleet = attachVehicleLayer(map)
      const chips = attachVehicleChips(map)

      if (cancelled) {
        chips.detach()

        return
      }

      const render = coalesceToFrames<ConsoleSnapshot>((next) => {
        setFleet(next.fleet, next.station)
        chips.update(next.fleet, next.station)
        setSnapshot(next)
      })

      detachChips = chips.detach
      cancelPending = render.cancel

      //  **The connection waits for the map.** Opening it alongside the map instead -- so the
      //  snapshot and the style parse in parallel -- costs the basemap entirely: MapLibre loads its
      //  worker from a script request the browser schedules at low priority, and an SSE stream is a
      //  response that never completes, so the scheduler leaves that request queued behind it. The
      //  symptom is not subtle and is not obviously about connections: the background paints, no data
      //  layer ever appears, and `load` never fires, indefinitely. Measured on this basemap at
      //  45 seconds and still waiting, against six with the connection opened here.
      disconnect = connectTelemetry(render.deliver)
    })

    return () => {
      cancelled = true
      disconnect?.()
      cancelPending?.()
      detachChips?.()
      map.remove()
      mapRef.current = null
    }
  }, [])

  return (
    <div className="console">
      <StationBar station={snapshot.station} />
      <div className="console-body">
        <div ref={containerRef} className="map" />
        <FleetPanel fleet={snapshot.fleet} station={snapshot.station} />
      </div>
    </div>
  )
}

export default App
