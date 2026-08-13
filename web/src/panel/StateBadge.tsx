import type { VehicleAppearance } from '../vehicles/appearance'
import {
  BADGE_OUTLINE_WIDTH,
  CENTRE,
  DART_PATH,
  DISC_RADIUS,
  DRAWING_EXTENT,
  RING_DASH,
  RING_GAP,
  RING_RADIUS,
} from '../vehicles/geometry'

/**
 * The state's marker, at the head of a panel row.
 *
 * The same five shapes the map draws, from the same geometry, so a row and a marker cannot come to
 * disagree about what a state looks like. SVG here rather than the canvas the map uses because the
 * panel is HTML already and this needs no bitmap.
 *
 * **Painted in `currentColor`.** The stylesheet colours the row from its `data-state`, which means
 * the badge, the id and the age chip take their colour from one rule rather than three — and it is
 * how the panel's live badge rests at the panel ink while the map's live marker stays the brightest
 * thing on the map. Those are different jobs: on the map, live has to beat a graticule; in a panel
 * of twelve rows, eleven healthy ones should contribute very little.
 *
 * The badge never rotates. It is a legend for the state, not an indication of heading — the map is
 * where a direction means something, and a heading in a fixed 18-pixel box would be unreadable
 * anyway.
 */
export function StateBadge({ appearance }: { appearance: VehicleAppearance }) {
  return (
    <svg className="badge" viewBox={`0 0 ${DRAWING_EXTENT} ${DRAWING_EXTENT}`} aria-hidden="true">
      {shapeOf(appearance)}
    </svg>
  )
}

function shapeOf(appearance: VehicleAppearance) {
  //  A dashed outline is only ever the ring, and a solid fill is only ever a body: the five
  //  combinations the language produces are exactly the branches below, and any other pairing would
  //  be a shape nobody designed.
  if (appearance.shape === 'ring') {
    return (
      <circle
        cx={CENTRE}
        cy={CENTRE}
        r={RING_RADIUS}
        fill="none"
        stroke="currentColor"
        strokeWidth={BADGE_OUTLINE_WIDTH}
        strokeDasharray={`${RING_DASH} ${RING_GAP}`}
      />
    )
  }

  const solid = appearance.fill === 'solid'

  if (appearance.shape === 'disc') {
    return (
      <circle
        cx={CENTRE}
        cy={CENTRE}
        r={DISC_RADIUS}
        fill={solid ? 'currentColor' : 'none'}
        stroke={solid ? 'none' : 'currentColor'}
        strokeWidth={BADGE_OUTLINE_WIDTH}
      />
    )
  }

  return (
    <path
      d={DART_PATH}
      fill={solid ? 'currentColor' : 'none'}
      stroke={solid ? 'none' : 'currentColor'}
      strokeWidth={BADGE_OUTLINE_WIDTH}
      strokeLinejoin="round"
    />
  )
}
