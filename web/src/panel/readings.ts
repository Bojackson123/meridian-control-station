import type { VehicleAppearance } from '../vehicles/appearance'
import type { VehicleFrame } from '../telemetry/types'

/**
 * The panel's numbers, formatted.
 *
 * Every one of these is fixed-width by construction, because the row is not. At twelve vehicles
 * reporting at 1 Hz a value that changes its own width is a column of jitter twelve rows deep, and
 * a display that shimmers is one an operator learns to stop looking at. The reserved widths in the
 * stylesheet and the fixed shapes here are the same decision arriving from two directions.
 *
 * **A dash is never a zero and a zero is never a dash.** `0.0` is a vehicle at rest and `000°` is
 * north; both are readings, and both are things the station may genuinely have been told. The
 * dashes below mean the opposite -- that there is no reading, either because the vehicle did not
 * send one or because the station has stopped believing the ones it has.
 */

/** No reading. Em dashes, one per digit the slot holds, so the absence is the same width as a value. */
const NO_READING = {
  speed: '——',
  heading: '———',
  battery: '——',
} as const

/**
 * The position, always shown.
 *
 * It is the one reading that survives a lost track: the marker is a record of where the vehicle
 * was, and a record with its coordinates removed is not a record. Four decimal places is about
 * eleven metres of latitude, which is finer than anything this console is used to judge.
 */
export function formatPosition(frame: VehicleFrame): string {
  return `${frame.latitudeDegrees.toFixed(4)} ${frame.longitudeDegrees.toFixed(4)}`
}

/**
 * The altitude, with the datum it was measured against (MCS-004).
 *
 * The reference travels with the number and is not abbreviated away. A bare altitude is a number an
 * operator has to guess the meaning of, and the guess that gets made is the one that matches the
 * terrain they are picturing.
 */
export function formatAltitude(frame: VehicleFrame): string {
  return `${Math.round(frame.altitude.meters)} ${frame.altitude.reference.toUpperCase()}`
}

/** Ground speed in metres per second, to one decimal — the unit is in the column label. */
export function formatSpeed(frame: VehicleFrame, appearance: VehicleAppearance): string {
  if (!appearance.assertsReadings || typeof frame.groundSpeedMetersPerSecond !== 'number') {
    return NO_READING.speed
  }

  return frame.groundSpeedMetersPerSecond.toFixed(1)
}

/**
 * The heading, three digits and a degree sign.
 *
 * Read from the appearance rather than from the frame, so the panel drops the heading at exactly
 * the moment the marker drops its nose. Two components deciding that separately is how a row comes
 * to read `087°` beside a marker that has stopped claiming to know.
 */
export function formatHeading(appearance: VehicleAppearance): string {
  if (appearance.headingDegrees === null) return NO_READING.heading

  return `${String(Math.round(appearance.headingDegrees) % 360).padStart(3, '0')}°`
}

/** Battery as a whole percentage. Never clamped: a 200% battery is a broken adapter, not a full one. */
export function formatBattery(frame: VehicleFrame, appearance: VehicleAppearance): string {
  if (!appearance.assertsReadings || typeof frame.batteryPercent !== 'number') {
    return NO_READING.battery
  }

  return `${Math.round(frame.batteryPercent)}%`
}
