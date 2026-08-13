import type { ConsoleState, VehicleAppearance } from '../vehicles/appearance'
import type { LinkStatus, VehicleFrame } from '../telemetry/types'
import { StateBadge } from './StateBadge'
import {
  formatAltitude,
  formatBattery,
  formatHeading,
  formatPosition,
  formatSpeed,
} from './readings'

/**
 * One vehicle's row: two lines, all six of MCS-001's fields, always.
 *
 * A row that hides four of them behind an expander is not displaying them. The alternative
 * considered and rejected was a collapsed row that expands on selection — quieter at twelve, but it
 * makes MCS-001 depend on where the operator last clicked, and a requirement satisfied only for the
 * selected vehicle is not satisfied.
 *
 * *"A healthy vehicle contributes very little"* is therefore a typographic job rather than a hiding
 * job. Line one is airy and carries the state, the id, the battery and the link; line two carries
 * the numbers at low contrast. The eye is pulled by luminance, not by reading.
 *
 * The row's height is fixed in the stylesheet and so are its columns, because twelve of these have
 * to fit in the panel without it scrolling and none of them may move when a vehicle goes quiet.
 */

/** The link glyph, by what the vehicle claimed about its own radio. */
const LINK_GLYPH: Readonly<Record<LinkStatus, string>> = {
  Healthy: '●',
  Degraded: '◐',
  Lost: '○',
}

/**
 * What the row says when it has no link status worth reporting.
 *
 * Not `○`. That glyph means the vehicle told the station its radio was in trouble, which is a
 * different fact from the station having stopped hearing the vehicle at all — and the second must
 * never be rendered as the first, because an operator reading a lost link would go looking at the
 * radio.
 */
const LINK_UNKNOWN_GLYPH = '–'

/**
 * The state in words, for anything that cannot see the badge.
 *
 * The note's rule is that no state is carried by colour alone, and shape is what answers it on
 * screen. This answers it for a screen reader, where neither channel exists.
 */
const STATE_WORDS: Readonly<Record<ConsoleState, string>> = {
  live: 'Live',
  stale: 'Stale',
  lost: 'Lost',
  unknown: 'Unknown — the station is not reporting',
}

export function VehicleRow({
  frame,
  appearance,
}: {
  frame: VehicleFrame
  appearance: VehicleAppearance
}) {
  //  A vehicle the station cannot vouch for has no link status to report either: the last frame
  //  before a link dies almost always says Healthy, and printing that beside a four-minute-old
  //  position is the display repeating a claim it knows to be out of date.
  const linkStatus = appearance.assertsReadings ? frame.linkStatus : null

  return (
    <li className="row" data-state={appearance.state} data-vehicle={frame.vehicleId}>
      <div className="r1">
        <StateBadge appearance={appearance} />
        <span className="vid">{frame.vehicleId}</span>

        {/* The slot is reserved whether or not a chip is in it. That is what stops the row shifting
            at the moment a vehicle goes quiet — the worst possible moment to shift. */}
        <span className="slot">
          {appearance.chip !== null && <span className="chip">{appearance.chip}</span>}
        </span>

        <span className="batt">{formatBattery(frame, appearance)}</span>
        <span
          className="link"
          data-link={linkStatus ?? 'Unknown'}
          aria-label={`Link ${linkStatus ?? 'unknown'}`}
        >
          {linkStatus === null ? LINK_UNKNOWN_GLYPH : LINK_GLYPH[linkStatus]}
        </span>
      </div>

      <div className="r2">
        <span className="visually-hidden">{STATE_WORDS[appearance.state]}</span>
        <span className="pos">{formatPosition(frame)}</span>
        <span className="alt">{formatAltitude(frame)}</span>
        <span className="spd">{formatSpeed(frame, appearance)}</span>
        <span className="hdg">{formatHeading(appearance)}</span>
      </div>
    </li>
  )
}
