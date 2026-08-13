import type { StationLink } from '../telemetry/client'

/**
 * The bar across the top of the console: whether the station is still talking.
 *
 * **This is the alert bar's slot**, occupied early. The design note gives the top 48 pixels to a
 * persistent bar outside both the map and the panel — structurally independent of the current view
 * rather than independent by anyone remembering to keep it visible — and alerts move into it when
 * there are alerts to surface. Until then it carries the one thing the console has to say at this
 * level, which is whether anything below it can be believed.
 *
 * It occupies its height whether or not there is anything wrong, for the same reason the note gives
 * the alert bar: a bar that appears when the first problem arrives is a layout shift that pushes
 * the whole console down at the least convenient possible moment, and its absence is
 * indistinguishable from a console that has stopped evaluating.
 *
 * The distinction it draws is the one the state language cannot draw on its own. A quiet vehicle in
 * a healthy fleet is stale — the station said so, and the other eleven rows are still moving. A
 * quiet station leaves every vehicle's age unknown and growing at once, and twelve rings with no
 * explanation would read as twelve aircraft lost rather than as one station unreachable. Those need
 * different responses from an operator, so they get different words.
 */
export function StationBar({ station }: { station: StationLink }) {
  const unreachable = station === 'unreachable'

  return (
    <div className="station-bar" data-station={station} role="status">
      <span className="station-mark" aria-hidden="true">
        {unreachable ? '▲' : '●'}
      </span>
      <span className="station-text">
        {unreachable ? 'STATION UNREACHABLE · reconnecting' : 'STATION CONNECTED'}
      </span>
    </div>
  )
}
