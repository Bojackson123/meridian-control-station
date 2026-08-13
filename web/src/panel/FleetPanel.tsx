import type { StationLink } from '../telemetry/client'
import type { Fleet } from '../telemetry/types'
import { appearanceOf } from '../vehicles/appearance'
import { VehicleRow } from './VehicleRow'

/**
 * The fleet, listed beside the map.
 *
 * **Sized so twelve rows fit without scrolling**, which is the load-bearing constraint of the whole
 * layout: a panel that scrolls at twelve is a panel where the vehicle that needs you is off screen.
 * At the minimum supported viewport of 1280×800 the arithmetic is
 *
 * ```
 *    48   station bar
 *    28   panel header
 *    21   column labels
 *   624   twelve rows at 52
 *    56   the abort block
 *   ----
 *   777   of 800 — 23 spare
 * ```
 *
 * and every term of it is spelled out in the stylesheet beside the rule that sets it. The first
 * pass of the design note sized the rows at 56, forgot the abort block, and came to a confident
 * 748; built, it was 25 px over the viewport it claimed to fit, and the thing hanging off the
 * bottom edge was abort. A thirteenth vehicle would scroll it — there cannot be one, and the store
 * rejects it, which is the same bound arriving from the other side.
 *
 * **Rows stay in stable vehicle-id order.** Sorting by attention — lost, then stale, then live —
 * was drawn and rejected: it puts the most urgent thing at the top exactly once, and thereafter
 * means the row you are reaching for moves while you reach for it. The panel's job is to be in the
 * same place every time.
 */
export function FleetPanel({ fleet, station }: { fleet: Fleet; station: StationLink }) {
  //  Sorted here rather than by the client, which holds a map keyed by id and has no opinion about
  //  order. localeCompare so MAV-002 sorts before MAV-010 by the digits rather than by the string's
  //  code units, which is the same answer today and stops being it the moment an adapter names a
  //  vehicle anything else.
  const vehicles = [...fleet.values()].sort((left, right) =>
    left.vehicleId.localeCompare(right.vehicleId, undefined, { numeric: true }),
  )

  return (
    <section className="panel" aria-label="Fleet">
      <header className="panel-hd">
        <b>FLEET</b>
        <span>{vehicles.length}</span>
      </header>

      {/* Column labels once, at the top, rather than a unit repeated twelve times. */}
      <div className="col-hd" aria-hidden="true">
        <i />
        <i>POSITION</i>
        <i>ALT</i>
        <i>SPD m/s</i>
        <i>HDG</i>
      </div>

      <ul className="rows">
        {vehicles.map((frame) => (
          <VehicleRow
            key={frame.vehicleId}
            frame={frame}
            appearance={appearanceOf(frame, station)}
          />
        ))}
      </ul>

      {/* The abort block's space, held and empty.
          "The operator can always reach abort" is a layout constraint rather than a feature, and
          layout constraints have to be honoured by the layout that gets built first — so the
          reservation is here from the start and the control drops into it when there is a command
          path for it to use. What is deliberately *not* here is a disabled ABORT button: a control
          that looks like the one thing an operator must be able to reach, and does nothing, is a
          worse thing to ship than a gap. */}
      <div className="abort-reserved" />
    </section>
  )
}
