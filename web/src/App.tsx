import { useEffect, useRef } from 'react'
import { MapLibreMap, ScaleControl } from 'maplibre-gl'

//  MapLibre's controls render as unstyled text without this. It is bundled, not linked, which is
//  the same reason as everything else here: the console must not reach off-origin for anything.
import 'maplibre-gl/dist/maplibre-gl.css'

import { attachGraticule } from './basemap/graticule'
import { configureMapLibreWorker } from './basemap/worker'
import './App.css'

/**
 * The console's map shell: a full-bleed MapLibre map on a basemap served entirely from this origin.
 *
 * There is nothing else on the page yet, and the emptiness is the point -- vehicles, state and
 * chrome arrive on top of a basemap that has already been proved to make no third-party requests.
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

    map.on('load', () => attachGraticule(map))

    return () => {
      map.remove()
      mapRef.current = null
    }
  }, [])

  return <div ref={containerRef} className="map" />
}

export default App
