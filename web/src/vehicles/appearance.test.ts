import { describe, expect, it } from 'vitest'

import type { VehicleFrame, VehicleState } from '../telemetry/types'
import { appearanceOf, formatAge } from './appearance'

/**
 * The state language, asserted.
 *
 * MCS-003 is verified by Test as well as by Inspection, and these are the Test half: that each
 * state resolves to a distinct rendering, that the stale one carries the age, and that no pair of
 * them is separated by colour alone. The Inspection half is `docs/notes/console-design.md`, which
 * these tests are written against rather than against the implementation -- a test that restates
 * the code it covers agrees with it no matter how wrong both are.
 *
 * Everything here is a pure call. No map, no station, no clock: {@link appearanceOf} is a function
 * of a frame and a connection status, and the transitions between states belong to the station and
 * are tested in `Mcs.Core.Tests` against `TelemetryCurrency`.
 */

/** A frame in whatever state is being asked about. Only the fields the language reads are meaningful. */
const frameIn = (state: VehicleState, overrides: Partial<VehicleFrame> = {}): VehicleFrame => ({
  vehicleId: 'MAV-001',
  latitudeDegrees: 34.7304,
  longitudeDegrees: -86.5861,
  altitude: { meters: 300, reference: 'Msl' },
  groundSpeedMetersPerSecond: 21.4,
  headingDegrees: 87.5,
  batteryPercent: 74,
  linkStatus: 'Healthy',
  state,
  ageMilliseconds: 0,
  receivedAtUtc: '2026-08-13T09:15:00.0000000+00:00',
  ...overrides,
})

describe('formatAge', () => {
  it('shows bare seconds below a minute', () => {
    expect(formatAge(7_400)).toBe('7s')
  })

  it('keeps the seconds below ten minutes, zero-padded so the slot does not jitter', () => {
    expect(formatAge(80_000)).toBe('1m 20s')
    expect(formatAge(65_000)).toBe('1m 05s')
  })

  it('drops the seconds past ten minutes', () => {
    expect(formatAge(12 * 60_000)).toBe('12m')
  })

  it('caps at an hour, so the reserved slot can never widen', () => {
    expect(formatAge(60 * 60_000)).toBe('1h+')
    expect(formatAge(400 * 60_000)).toBe('1h+')
  })

  //  The longest string the slot has to hold. If a format is ever added that beats this, the
  //  reserved width in the panel is wrong and the row will move when a vehicle goes quiet.
  it('never renders wider than "1m 05s"', () => {
    const widest = Math.max(
      ...[0, 9_000, 59_999, 60_000, 599_999, 600_000, 3_599_999, 3_600_000].map(
        (age) => formatAge(age).length,
      ),
    )

    expect(widest).toBe('1m 05s'.length)
  })
})

describe('appearanceOf', () => {
  it('draws a live vehicle solid, pointed, and with no chip at all', () => {
    const live = appearanceOf(frameIn('Live'), 'connected')

    expect(live.state).toBe('live')
    expect(live.shape).toBe('dart')
    expect(live.fill).toBe('solid')
    expect(live.headingDegrees).toBe(87.5)

    //  Not "0s", not a dash. The chip's arrival is itself a channel.
    expect(live.chip).toBeNull()
  })

  it('draws a stale vehicle hollow, still pointed, and carrying its age', () => {
    const stale = appearanceOf(frameIn('Stale', { ageMilliseconds: 7_400 }), 'connected')

    expect(stale.state).toBe('stale')
    expect(stale.fill).toBe('hollow')

    //  Three seconds of silence is a gap, not a loss: the last reported heading is still the best
    //  answer available, so the dart stays and freezes.
    expect(stale.shape).toBe('dart')
    expect(stale.headingDegrees).toBe(87.5)

    //  MCS-003: the stale state includes the age.
    expect(stale.chip).toBe('7s')
  })

  it('draws a lost vehicle as a dashed ring with no heading at all', () => {
    const lost = appearanceOf(
      frameIn('Lost', { ageMilliseconds: 252_000, headingDegrees: 87.5 }),
      'connected',
    )

    expect(lost.state).toBe('lost')
    expect(lost.shape).toBe('ring')
    expect(lost.fill).toBe('dashed')
    expect(lost.chip).toBe('4m 12s')

    //  The frame still carries a heading and it is still dropped. The station does not know which
    //  way that aircraft is pointing; it knows where it was pointing four minutes ago, and a
    //  confident nose on a dead track is the display asserting what it cannot support.
    expect(lost.headingDegrees).toBeNull()
  })

  it('takes the nose off a reporting vehicle that has not said which way it faces', () => {
    for (const state of ['Live', 'Stale'] as const) {
      const appearance = appearanceOf(frameIn(state, { headingDegrees: null }), 'connected')

      //  A disc, not a dart pointed north. Zero is a direction, and the vehicle reported none.
      expect(appearance.shape).toBe('disc')
      expect(appearance.headingDegrees).toBeNull()
    }
  })

  //  The note's rule, as an assertion rather than as an intention: around one man in twelve has
  //  some colour vision deficiency, and a screenshot pasted into a report loses hue entirely for
  //  everyone. Every pair below must differ somewhere other than in `colour`.
  it('separates every pair of states in more than one channel, and never in colour alone', () => {
    const states = [
      appearanceOf(frameIn('Live'), 'connected'),
      appearanceOf(frameIn('Stale', { ageMilliseconds: 7_000 }), 'connected'),
      appearanceOf(frameIn('Lost', { ageMilliseconds: 252_000 }), 'connected'),
    ]

    for (const [index, left] of states.entries()) {
      for (const right of states.slice(index + 1)) {
        const channels = [
          left.shape !== right.shape,
          left.fill !== right.fill,
          left.colour !== right.colour,
          (left.chip === null) !== (right.chip === null),
          (left.headingDegrees === null) !== (right.headingDegrees === null),
        ]

        const colourless = channels.filter((_, channel) => channel !== 2)

        expect(
          colourless.some(Boolean),
          `${left.state} and ${right.state} differ only in colour`,
        ).toBe(true)

        expect(
          channels.filter(Boolean).length,
          `${left.state} and ${right.state} share too many channels`,
        ).toBeGreaterThanOrEqual(2)
      }
    }
  })

  it('draws live above stale above lost, so a live vehicle is never obscured', () => {
    const sortKeys = (['Live', 'Stale', 'Lost'] as const).map(
      (state) => appearanceOf(frameIn(state), 'connected').sortKey,
    )

    expect(sortKeys[0]).toBeGreaterThan(sortKeys[1])
    expect(sortKeys[1]).toBeGreaterThan(sortKeys[2])
  })
})

describe('appearanceOf, with the station unreachable', () => {
  //  The failure this case exists for: the last thing the station said was that everything was
  //  live, and it has not been able to say anything since. Rendering the fleet as live on the
  //  strength of that snapshot is HAZ-01 exactly.
  it('demotes every vehicle whatever its last reported state', () => {
    for (const state of ['Live', 'Stale', 'Lost'] as const) {
      const appearance = appearanceOf(frameIn(state), 'unreachable')

      expect(appearance.state).toBe('unknown')
      expect(appearance.shape).toBe('ring')
      expect(appearance.fill).toBe('dashed')
      expect(appearance.headingDegrees).toBeNull()
    }
  })

  it('shows no age, because the console has none to show', () => {
    const appearance = appearanceOf(frameIn('Live', { ageMilliseconds: 200 }), 'unreachable')

    //  The frame's own age is 200 ms and it is not rendered. It was true when the station last
    //  spoke and has been growing by an amount only the station could measure ever since, so the
    //  chip says the one honest thing available.
    expect(appearance.chip).toBe('?')
  })

  it('is told apart from lost by the chip alone, which is why the station bar exists', () => {
    const lost = appearanceOf(frameIn('Lost', { ageMilliseconds: 60_000 }), 'connected')
    const unknown = appearanceOf(frameIn('Lost', { ageMilliseconds: 60_000 }), 'unreachable')

    //  Deliberately the same rendering: "this position is a record rather than a location" is
    //  already what the ring says, and a fifth marker shape would be a new thing to defend for a
    //  case the existing one describes. The age is what differs, and it differs by being absent.
    expect(unknown.shape).toBe(lost.shape)
    expect(unknown.fill).toBe(lost.fill)
    expect(unknown.colour).toBe(lost.colour)
    expect(unknown.chip).not.toBe(lost.chip)
  })
})
