import type { Map as MapLibreMap } from 'maplibre-gl'

import type { StationLink } from '../telemetry/client'
import type { Fleet } from '../telemetry/types'
import { appearanceOf } from './appearance'

/**
 * The age chips on the map: how old the data behind a marker is, beside the marker.
 *
 * **These cannot be MapLibre labels, and that is a constraint rather than a preference.** The
 * basemap style has no `glyphs` key on purpose, and MapLibre fetches glyph range files from that
 * URL the moment any layer uses a text field — so a labelled symbol layer is a layer that reaches
 * off-origin, which the console is required never to do and `index.html`'s `default-src 'self'`
 * would fail loudly. The chips are therefore DOM nodes positioned over the canvas, using the
 * projection the map already exposes.
 *
 * That is affordable at exactly this scale and not much beyond it: twelve nodes updating at 1 Hz
 * is nothing, and the thing to avoid is a node per frame of history rather than a node per vehicle.
 * A chip belongs to a vehicle and is reused for as long as that vehicle is in the fleet.
 *
 * MCS-003 requires the stale state to *include* the age, and a hover is not an inclusion — an
 * operator scanning twelve vehicles hovers over none of them. That is the whole reason this module
 * exists rather than a title attribute.
 */

/** How far right of the marker's centre the chip sits, in CSS pixels. Clear of a 24 px marker. */
const CHIP_OFFSET_X = 20

/** How far above it. Small: the chip is beside the marker, not above it. */
const CHIP_OFFSET_Y = -8

/** What one chip needs to be redrawn where it belongs, without going back to the fleet. */
interface ChipState {
  element: HTMLElement
  latitudeDegrees: number
  longitudeDegrees: number
}

/**
 * Adds the chip overlay to a map and returns the function that keeps it current.
 *
 * @returns An updater, and a disposer for the map listener it registers.
 */
export function attachVehicleChips(map: MapLibreMap): {
  update: (fleet: Fleet, station: StationLink) => void
  detach: () => void
} {
  const layer = document.createElement('div')
  layer.className = 'chips'

  //  Inside the canvas container rather than over the whole map, so the chips sit under MapLibre's
  //  own controls -- the scale bar is a distance reference and an age chip drifting across it would
  //  cover the one thing on this basemap that says how big anything is.
  map.getCanvasContainer().append(layer)

  const chips = new Map<string, ChipState>()

  const place = (chip: ChipState) => {
    const at = map.project([chip.longitudeDegrees, chip.latitudeDegrees])

    //  Rounded to whole pixels. Subpixel positions blur small text, and a chip that resharpens as
    //  the map settles reads as a rendering fault on a display whose whole job is to look
    //  trustworthy.
    chip.element.style.transform =
      `translate(${Math.round(at.x + CHIP_OFFSET_X)}px, ${Math.round(at.y + CHIP_OFFSET_Y)}px)`
  }

  //  Repositioned as the map moves, not as data arrives. A chip is anchored to a place on the
  //  ground, and a pan that left twelve of them behind would be twelve numbers pointing at the
  //  wrong aircraft -- briefly, which is the worst duration for it.
  const reposition = () => {
    for (const chip of chips.values()) place(chip)
  }

  map.on('move', reposition)

  return {
    update: (fleet: Fleet, station: StationLink) => {
      for (const frame of fleet.values()) {
        const appearance = appearanceOf(frame, station)
        const held = chips.get(frame.vehicleId)

        //  A live vehicle has no chip at all -- not "0s", not a dash. The node goes rather than
        //  being emptied, so nothing is left to leak a stale width or a stale colour.
        if (appearance.chip === null) {
          held?.element.remove()
          chips.delete(frame.vehicleId)

          continue
        }

        const chip: ChipState = held ?? {
          element: newChipElement(layer),
          latitudeDegrees: frame.latitudeDegrees,
          longitudeDegrees: frame.longitudeDegrees,
        }

        chip.latitudeDegrees = frame.latitudeDegrees
        chip.longitudeDegrees = frame.longitudeDegrees
        chip.element.textContent = appearance.chip

        //  The colour comes from the stylesheet, keyed on this, so the map's chips and the panel's
        //  cannot be given different amber by two people on two days.
        chip.element.dataset.state = appearance.state

        chips.set(frame.vehicleId, chip)
        place(chip)
      }

      //  A vehicle the station has dropped takes its chip with it. Without this the number would
      //  outlive the marker, which is the one thing on screen worse than the marker outliving the
      //  data.
      for (const [vehicleId, chip] of chips) {
        if (fleet.has(vehicleId)) continue

        chip.element.remove()
        chips.delete(vehicleId)
      }
    },

    detach: () => {
      map.off('move', reposition)
      layer.remove()
      chips.clear()
    },
  }
}

function newChipElement(layer: HTMLElement): HTMLElement {
  const element = document.createElement('span')
  element.className = 'map-chip'

  layer.append(element)

  return element
}
