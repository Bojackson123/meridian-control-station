import type { StationLink } from '../telemetry/client'
import type { VehicleFrame } from '../telemetry/types'
import { AMBER, INK_BRIGHT, INK_DIM } from '../palette'

/**
 * The state language, as one function: what a vehicle looks like, given the station's judgement of
 * how current its data is and whether the station is still talking.
 *
 * **Both surfaces come through here.** The map layer and the fleet panel call
 * {@link appearanceOf} and render what it returns; neither derives a state of its own. Two
 * components working it out independently — one from `state`, one from the age — is how they end up
 * disagreeing at the boundary, showing a marker that has gone amber beside a row that has not,
 * which is HAZ-01 in miniature (MCS-003).
 *
 * Pure, and deliberately so. There is no clock here, no DOM and no map: the whole language is a
 * function of a frame and a connection status, which is what lets every transition be tested
 * directly. If this ever needs a timer to be correct, the state has been stored somewhere it
 * should not be.
 *
 * The design it implements is `docs/notes/console-design.md` §2 and §3, and its working drawing is
 * `docs/notes/console-design/mockup.html`. Where this file and the note disagree, one of them is
 * wrong and both get looked at.
 */

/**
 * What the console says about a vehicle.
 *
 * The first three are the station's answer, arriving on the wire. {@link Unknown} is the console's
 * own, and is a statement about the *station* rather than about a vehicle — see
 * {@link appearanceOf}.
 */
export type ConsoleState = 'live' | 'stale' | 'lost' | 'unknown'

/**
 * The marker's outline.
 *
 * `disc` is a dart with its nose taken off, for a vehicle that is reporting but has not said which
 * way it is pointing — position and velocity arrive in separate messages at separate rates, so
 * that is ordinary rather than exceptional. `ring` is the shape a track gets when the station has
 * stopped hearing from it at all; it says *a vehicle was here* and declines to say anything
 * further, which is the honest content of the data.
 */
export type MarkerShape = 'dart' | 'disc' | 'ring'

/**
 * How the outline is painted, and the channel that carries currency.
 *
 * `solid` means *the data is current*. It never means *nothing is wrong* — a vehicle with a
 * geofence breach and a flat battery is still drawn solid while it is reporting, because the fill
 * is an assertion about the age of the data and nothing else may be allowed to override it.
 */
export type MarkerFill = 'solid' | 'hollow' | 'dashed'

/** How a vehicle is drawn, on both surfaces. */
export interface VehicleAppearance {
  /** The state the rest of these follow from. */
  state: ConsoleState

  shape: MarkerShape

  fill: MarkerFill

  /**
   * The state's colour on the map, from the note's §2 table.
   *
   * The panel takes its colours from the stylesheet instead, keyed on `state`: a live vehicle
   * rests at the panel's ordinary ink rather than at this, so that eleven healthy rows do not each
   * carry the brightest glyph on the surface. On the map the opposite is wanted — live is the
   * brightest thing there, and that is what keeps it above the graticule.
   */
  colour: string

  /**
   * Which marker draws above which, when two overlap: live above stale above lost.
   *
   * Luminance already encodes confidence, and this is the same ordering in the third dimension. A
   * live vehicle is never obscured by the ring of one that stopped reporting ten minutes ago.
   */
  sortKey: number

  /**
   * The age chip's text, or `null` for no chip at all.
   *
   * **Not `0s`, not a dash — nothing.** The chip's *appearance* is therefore itself a state
   * change, which is a third channel for free, and it keeps the calm case genuinely calm: eleven
   * quiet rows and one that grew a number.
   */
  chip: string | null

  /**
   * Whether the vehicle's own reported values may still be shown as readings.
   *
   * False once the station has lost the vehicle, and the panel shows dashes instead. Position is
   * the exception and is always shown, because it is the *record* of where the vehicle was and the
   * marker beside it says how old that record is. Speed, heading and battery are not records: they
   * describe a state that only means anything as a current claim, and a frozen `21.4` beside a
   * four-minute-old position reads as an aircraft still flying at cruise.
   *
   * Stale keeps its readings. Three seconds of silence is a gap, not a loss, and the last reported
   * numbers are still the best answers available — which is the same argument that keeps its dart.
   */
  assertsReadings: boolean

  /**
   * Which way the marker points, or `null` for a marker that declines to point.
   *
   * Dropping the heading is the strongest non-colour channel available and it is also just true:
   * the station does not know which way a silent aircraft is pointing, it knows where it was
   * pointing some minutes ago. A confident nose on a dead track is the display asserting something
   * it cannot support.
   */
  headingDegrees: number | null
}

/** What the chip says when the console has no station to get an age from. */
const UNKNOWN_AGE = '?'

const MILLISECONDS_PER_SECOND = 1_000
const SECONDS_PER_MINUTE = 60
const SECONDS_PER_HOUR = 3_600

/** Beyond ten minutes nobody is reading the seconds off the gap, so they stop being shown. */
const SECONDS_AT_WHICH_SECONDS_STOP_MATTERING = 10 * SECONDS_PER_MINUTE

/**
 * The age of the data, at three magnitudes and one cap (note §3).
 *
 * ```
 *     7s        under a minute — seconds, no padding
 *  1m 20s       under ten minutes — the seconds still matter here
 *     12m       beyond that — whole minutes
 *     1h+       one cap, so the slot can never widen
 * ```
 *
 * The cap is the point. The chip sits in a slot of reserved width, and a slot that can widen is a
 * row that moves at the moment a vehicle goes quiet — which is the worst possible moment for the
 * thing the operator is reaching for to move.
 *
 * A negative age is rendered as one rather than clamped to zero. It cannot arrive from a healthy
 * station — `TelemetryCurrency` refuses to construct one — so seeing `-4s` on screen means the
 * station is broken, and a console that quietly rounded that up to `0s` would be reporting the
 * broken station as the freshest vehicle in the fleet.
 *
 * @param ageMilliseconds How long ago the station received the frame, by the station's clock.
 */
export function formatAge(ageMilliseconds: number): string {
  const seconds = Math.floor(ageMilliseconds / MILLISECONDS_PER_SECOND)

  if (seconds < SECONDS_PER_MINUTE) return `${seconds}s`

  const minutes = Math.floor(seconds / SECONDS_PER_MINUTE)

  if (seconds < SECONDS_AT_WHICH_SECONDS_STOP_MATTERING) {
    return `${minutes}m ${String(seconds % SECONDS_PER_MINUTE).padStart(2, '0')}s`
  }

  if (seconds < SECONDS_PER_HOUR) return `${minutes}m`

  return '1h+'
}

/**
 * Resolves one vehicle's appearance.
 *
 * `frame.state` is read, never recomputed. The threshold arithmetic belongs to the station, which
 * owns the only clock this console may trust — a browser thirty seconds out would otherwise render
 * a live aircraft as lost or, far worse, a lost one as live. `ageMilliseconds` is used for the
 * chip's text and for nothing else.
 *
 * **An unreachable station demotes the whole fleet**, whatever each frame last said about itself.
 * A vehicle that has gone quiet in a healthy fleet is stale because the station said so; a station
 * that has gone quiet leaves every vehicle's age unknown and growing, and going on showing the
 * fleet as live because the last snapshot said so is exactly the failure this language exists to
 * prevent. The demoted rendering is lost's — a dashed ring, no heading, dimmest — because that is
 * already the language's way of saying *this position is a record rather than a location*, and
 * inventing a fifth marker would be a new shape to defend for a case the existing one describes.
 * What separates the two is the chip: lost carries a measured age, and this carries
 * {@link UNKNOWN_AGE}, because the console cannot measure one and must not invent one. The station
 * bar says the rest.
 */
export function appearanceOf(frame: VehicleFrame, station: StationLink): VehicleAppearance {
  if (station === 'unreachable') return NOTHING_TO_CLAIM

  switch (frame.state) {
    case 'Lost':
      return {
        state: 'lost',
        shape: 'ring',
        fill: 'dashed',
        colour: INK_DIM,
        sortKey: 1,
        assertsReadings: false,
        chip: formatAge(frame.ageMilliseconds),
        headingDegrees: null,
      }

    case 'Stale':
      return {
        state: 'stale',
        shape: shapeFor(frame.headingDegrees),
        fill: 'hollow',
        colour: AMBER,
        sortKey: 2,
        assertsReadings: true,

        //  Stale keeps its dart and freezes the heading where it stopped: three seconds of silence
        //  is a gap, not a loss, and the last reported heading is still the best answer available.
        chip: formatAge(frame.ageMilliseconds),
        headingDegrees: frame.headingDegrees,
      }

    case 'Live':
      return {
        state: 'live',
        shape: shapeFor(frame.headingDegrees),
        fill: 'solid',
        colour: INK_BRIGHT,
        sortKey: 3,
        assertsReadings: true,
        chip: null,
        headingDegrees: frame.headingDegrees,
      }

    //  A state this console does not recognise, which means the API has grown one since this file
    //  was written. It is not evidence of currency, so it is not rendered as any: an unrecognised
    //  claim gets the same treatment as an absent one. The alternative -- falling through to
    //  `undefined` and letting the map or the panel decide what to do with it -- ends up rendering
    //  the vehicle in whichever state the renderer's default happens to be, and the default nobody
    //  chooses is always the first one in the list.
    default:
      return NOTHING_TO_CLAIM
  }
}

/**
 * The rendering for a vehicle the console has nothing current to say about.
 *
 * Frozen and shared: it carries no per-vehicle information by construction, which is the property
 * that makes it correct. Anything that varied here would be a claim, and having none to make is
 * the whole state.
 */
const NOTHING_TO_CLAIM: VehicleAppearance = Object.freeze({
  state: 'unknown',
  shape: 'ring',
  fill: 'dashed',
  colour: INK_DIM,
  sortKey: 1,
  assertsReadings: false,
  chip: UNKNOWN_AGE,
  headingDegrees: null,
})

/**
 * A nose only where there is a heading to point it at.
 *
 * Tested for being a number rather than against `null`, because a frame is cast from `JSON.parse`
 * and never validated: absence reaches here as `undefined` the moment anything upstream omits the
 * key instead of nulling it. `=== null` would miss that and draw a dart pointing at the fallback
 * of north, which is a confident claim about a direction nothing reported.
 */
function shapeFor(headingDegrees: number | null): MarkerShape {
  return typeof headingDegrees === 'number' ? 'dart' : 'disc'
}
