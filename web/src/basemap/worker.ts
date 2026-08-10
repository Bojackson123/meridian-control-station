import { setWorkerUrl } from 'maplibre-gl'

//  Vite bundles the worker and its dependencies into one chunk it can name, and hands back a URL on
//  this origin. `?worker` is what makes it a worker build rather than a copied file: the worker's own
//  relative import of maplibre-gl-shared.mjs is resolved into the chunk, which a plain asset copy
//  would leave dangling next to a file that is no longer its sibling.
import maplibreWorkerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?worker&url'

/**
 * Points MapLibre at a worker this application serves.
 *
 * Left alone, MapLibre derives its worker URL at runtime from `import.meta.url` and looks for a
 * sibling file. No bundler can see through that, so neither the dev server nor the production build
 * emits the worker, and the request for it hangs: the basemap paints and every data layer stays
 * silently empty. The symptom is a map that looks like a styling mistake rather than a missing
 * thread, which is why this is configured explicitly rather than left to a default that happens to
 * work when the library is loaded unbundled.
 *
 * Idempotent -- it sets one value in MapLibre's global config, so calling it before each map is
 * cheaper than reasoning about whether it already ran.
 */
export function configureMapLibreWorker(): void {
  setWorkerUrl(maplibreWorkerUrl)
}
