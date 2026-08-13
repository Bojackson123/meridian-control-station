import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import type { VehicleFrame, VehicleState } from '../telemetry/types'
import { appearanceOf } from '../vehicles/appearance'
import { VehicleRow } from './VehicleRow'

/**
 * The row, as an operator would read it.
 *
 * `appearance.test.ts` proves the language; this proves it reaches the screen. The two halves are
 * worth keeping apart: a descriptor that says `chip: '7s'` is not an age on screen until something
 * renders it, and MCS-003 requires the second.
 *
 * Rendered through the real `appearanceOf` rather than a hand-built appearance, so a row and a
 * marker cannot be shown to agree by a test that fed them different things.
 */

afterEach(cleanup)

const frameIn = (state: VehicleState, overrides: Partial<VehicleFrame> = {}): VehicleFrame => ({
  vehicleId: 'MAV-003',
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

const renderRow = (frame: VehicleFrame, station: 'connected' | 'unreachable' = 'connected') => {
  render(
    <ul>
      <VehicleRow frame={frame} appearance={appearanceOf(frame, station)} />
    </ul>,
  )

  return document.querySelector('.row')!
}

describe('a live row', () => {
  it('shows all six of MCS-001 fields and no chip', () => {
    const row = renderRow(frameIn('Live'))

    expect(row.getAttribute('data-state')).toBe('live')
    expect(screen.getByText('MAV-003')).toBeDefined()
    expect(screen.getByText('34.7304 -86.5861')).toBeDefined()
    expect(screen.getByText('300 MSL')).toBeDefined()
    expect(screen.getByText('21.4')).toBeDefined()
    expect(screen.getByText('088°')).toBeDefined()
    expect(screen.getByText('74%')).toBeDefined()
    expect(screen.getByLabelText('Link Healthy')).toBeDefined()

    //  Not "0s", not a dash. The chip's absence is a channel.
    expect(row.querySelector('.chip')).toBeNull()
  })

  //  The slot the chip will appear in is reserved whether or not there is a chip in it, so the row
  //  does not shift at the moment a vehicle goes quiet.
  it('keeps the chip slot even with nothing in it', () => {
    const row = renderRow(frameIn('Live'))

    expect(row.querySelector('.slot')).not.toBeNull()
  })
})

describe('a stale row', () => {
  //  MCS-003: the stale state includes the age, on screen and not in a tooltip.
  it('renders the age', () => {
    renderRow(frameIn('Stale', { ageMilliseconds: 7_400 }))

    expect(screen.getByText('7s')).toBeDefined()
  })

  it('keeps its readings, because three seconds of silence is a gap and not a loss', () => {
    renderRow(frameIn('Stale', { ageMilliseconds: 7_400 }))

    expect(screen.getByText('21.4')).toBeDefined()
    expect(screen.getByText('088°')).toBeDefined()
    expect(screen.getByText('74%')).toBeDefined()
  })

  //  The note's rule, at the DOM: a state may never be carried by colour alone. The stylesheet is
  //  what colours these, so the channels a test can see are the badge's geometry and the chip.
  it('differs from live in more than the colour the stylesheet will give it', () => {
    const live = renderRow(frameIn('Live'))
    const liveBadge = live.querySelector('.badge')!.innerHTML
    const liveHasChip = live.querySelector('.chip') !== null

    cleanup()

    const stale = renderRow(frameIn('Stale', { ageMilliseconds: 4_000 }))
    const staleBadge = stale.querySelector('.badge')!.innerHTML
    const staleHasChip = stale.querySelector('.chip') !== null

    expect(staleBadge).not.toBe(liveBadge)
    expect(staleHasChip).not.toBe(liveHasChip)
  })
})

describe('a lost row', () => {
  it('shows the age and drops every reading that only means anything as a current one', () => {
    const row = renderRow(frameIn('Lost', { ageMilliseconds: 252_000 }))

    expect(screen.getByText('4m 12s')).toBeDefined()

    //  Dashes, not the last numbers the vehicle sent. A frozen 21.4 beside a four-minute-old
    //  position reads as an aircraft still flying at cruise.
    expect(row.querySelector('.spd')!.textContent).toBe('——')
    expect(row.querySelector('.hdg')!.textContent).toBe('———')
    expect(row.querySelector('.batt')!.textContent).toBe('——')

    //  The position stays. It is the record of where the vehicle was, and the chip beside it says
    //  how old that record is -- a record with its coordinates removed is not a record.
    expect(screen.getByText('34.7304 -86.5861')).toBeDefined()

    //  Not the Lost link glyph: the vehicle never claimed its radio was in trouble, the station
    //  stopped hearing it. Rendering the second as the first sends an operator to look at a radio.
    expect(row.querySelector('.link')!.getAttribute('data-link')).toBe('Unknown')
  })
})

describe('a row with no heading reported', () => {
  it('shows dashes rather than north, while keeping the speed it did report', () => {
    renderRow(frameIn('Live', { headingDegrees: null }))

    expect(screen.getByText('———')).toBeDefined()
    expect(screen.getByText('21.4')).toBeDefined()
  })
})

describe('every row, while the station is unreachable', () => {
  it('shows no age and says so to a reader that cannot see the badge', () => {
    const row = renderRow(frameIn('Live'), 'unreachable')

    expect(row.getAttribute('data-state')).toBe('unknown')
    expect(screen.getByText('?')).toBeDefined()
    expect(screen.getByText('Unknown — the station is not reporting')).toBeDefined()
  })
})
