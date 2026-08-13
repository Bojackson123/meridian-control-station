import type { Fleet, VehicleFrame } from './types'

/**
 * The console's one connection to the station: a snapshot of where everything is, then a live stream
 * of everything that happens next.
 *
 * This module owns the connection and nothing else -- it does not draw, and it holds no React state.
 * The separation is what lets the map be updated imperatively at frame rate without re-rendering a
 * component tree to hand MapLibre data it will copy into its own buffers anyway.
 */

/** The latest frame per known vehicle. Mirrors `TelemetryEndpoints.SnapshotPath`. */
const SNAPSHOT_PATH = '/api/vehicles'

/** Frames as they arrive. Mirrors `TelemetryEndpoints.StreamPath`. */
const STREAM_PATH = '/api/telemetry/stream'

/** The named SSE event carrying the vehicle that just reported. Mirrors `TelemetryEndpoints.TelemetryEventType`. */
const TELEMETRY_EVENT_TYPE = 'telemetry'

/**
 * The event carrying the whole fleet with its ages re-evaluated. Mirrors
 * `TelemetryEndpoints.FleetEventType`.
 *
 * This is how a vehicle that has stopped reporting is reported. Frames only arrive from vehicles
 * that are talking, so silence can never be delivered by the silent party -- the station says it on
 * a schedule instead. It also replaces the old empty `heartbeat`, and keeps that event's other job
 * of proving the connection is alive.
 */
const FLEET_EVENT_TYPE = 'fleet'

/**
 * How long the stream may say nothing at all -- no frame, no fleet tick -- before it is treated as
 * dead and reopened.
 *
 * Now many multiples of the station's one-second tick rather than two of the old fifteen-second
 * heartbeat, so this is a very patient number for what it now watches. Left where it is
 * deliberately: lowering it changes what the console does when the *station* goes away, as opposed
 * to a vehicle, and those need to be different pictures rather than the same one arriving sooner.
 */
const SILENCE_TIMEOUT_MS = 40_000

/**
 * How long to wait before reopening a stream the browser has given up on. Matches the retry interval
 * `EventSource` uses for the drops it does handle itself, so an outage recovers at one cadence rather
 * than two.
 */
const REOPEN_DELAY_MS = 3_000

/** Splits a station timestamp into whole milliseconds and the 100 ns ticks below them. */
const arrivalOf = (receivedAtUtc: string): [number, number] => {
  //  At most four digits past the third, which is exactly the resolution of the tick the station
  //  stamps with; padded so ".12" and ".1200" are the same fraction rather than 12 against 1200.
  const belowMilliseconds = /\.\d{3}(\d{1,4})/.exec(receivedAtUtc)

  return [
    Date.parse(receivedAtUtc),
    belowMilliseconds ? Number(belowMilliseconds[1].padEnd(4, '0')) : 0,
  ]
}

/**
 * Orders two arrival times, negative when `left` arrived first, at the precision the wire carries.
 *
 * `Date.parse` alone is the obvious way to do this and stops at whole milliseconds, discarding the
 * rest of a `DateTimeOffset` -- which is serialised to 100 ns. Two frames of one vehicle that
 * arrived a fraction of a millisecond apart then compare *equal*, and equal means "the station has
 * re-stated the frame I already hold" to the rule below, which settles it on age and throws the
 * newer position away. That is a frame the station received in full, dropped by the console, and the
 * conditions for it are not exotic: frames arriving in a burst is what produces sub-millisecond
 * gaps, and a burst is what draining a slow connection's backlog looks like.
 *
 * The digits below the millisecond are carried as a second number rather than added into the first.
 * A wall-clock reading is around 1.7e12 milliseconds, where a double's own step is already coarser
 * than the fraction being added, so the addition would round out the distinction it was made for.
 */
const compareArrival = (left: string, right: string): number => {
  const [leftMilliseconds, leftFraction] = arrivalOf(left)
  const [rightMilliseconds, rightFraction] = arrivalOf(right)

  return leftMilliseconds - rightMilliseconds || leftFraction - rightFraction
}

/**
 * Opens the station connection and reports the fleet whenever it changes.
 *
 * Both paths are relative on purpose. The dev server proxies `/api` to the API and nginx does the
 * same in the deployed stack, so this file needs no notion of an environment and no base URL to
 * configure wrongly.
 *
 * Recovery is divided three ways, and the divisions are the whole design. A dropped connection is
 * `EventSource`'s own to retry, and is left alone. A connection *attempt* answered with an HTTP
 * status is one the spec makes it abandon permanently, so that case is reopened on a timer. A
 * connection that stays open while saying nothing is invisible to both, and is caught by the
 * silence watchdog. Each of the three is commented where it lives; the first is the only one the
 * browser handles for you, and assuming it covers the other two is what leaves an operator watching
 * a console that stopped updating some minutes ago.
 *
 * Note what this does not do: nothing here draws. Every frame now arrives carrying the station's
 * judgement of how current it is, and while the station is unreachable the last frames stay on the
 * map at their last positions with ages that have stopped advancing. Turning both of those into
 * something an operator can see is the state language's job, and it is not built yet -- so for now
 * the disconnect is visible in the browser console and nowhere else.
 *
 * @param onFleet Called with the whole fleet after every accepted frame.
 * @returns A disposer. Call it once; the connection is not reusable afterwards.
 */
export function connectTelemetry(onFleet: (fleet: Fleet) => void): () => void {
  const fleet = new Map<string, VehicleFrame>()

  let stream: EventSource | null = null
  let snapshotRequest: AbortController | null = null
  let reopenTimer: ReturnType<typeof setTimeout> | undefined
  let silenceTimer: ReturnType<typeof setTimeout> | undefined
  let disposed = false

  //  A fresh map each time rather than the live one. Twelve entries makes the copy free, and handing
  //  out a value whose identity changes is what any future subscriber -- a vehicle panel through
  //  useSyncExternalStore -- needs to see that anything happened at all.
  const publish = () => onFleet(new Map(fleet))

  //  Later wins, by the station's clock rather than arrival order. This is what makes the race
  //  between the snapshot and the stream a non-problem instead of something to sequence carefully:
  //  a snapshot that lands after a newer streamed frame cannot walk the vehicle backwards.
  //
  //  "Later" has two parts, because the same frame now arrives repeatedly with its age advancing.
  //  A frame received earlier than the held one is rejected outright; the *same* frame is accepted
  //  only when it comes with a greater age, which is a fresher evaluation of the same data. Testing
  //  the receipt time alone would either throw away every fleet tick for a quiet vehicle -- the
  //  ones that matter -- or let a snapshot in flight reset an age that had already climbed past it.
  const admit = (frame: VehicleFrame): boolean => {
    const held = fleet.get(frame.vehicleId)

    if (held) {
      const order = compareArrival(held.receivedAtUtc, frame.receivedAtUtc)

      if (order > 0) return false
      if (order === 0 && held.ageMilliseconds >= frame.ageMilliseconds) return false
    }

    fleet.set(frame.vehicleId, frame)
    return true
  }

  //  The station's whole answer, so it is applied as one: every vehicle in it takes the state and
  //  age given, and every vehicle *not* in it has been dropped by the station and leaves the map.
  //  That last part is the only way a vehicle is ever removed here -- the store's contract says a
  //  subscription carries frames and a removal is not one, so without this a forgotten vehicle
  //  would sit on the map at its last position until the page was reloaded.
  const replaceFleet = (frames: VehicleFrame[]) => {
    const present = new Set(frames.map((frame) => frame.vehicleId))

    let changed = false
    for (const frame of frames) changed = admit(frame) || changed

    for (const id of [...fleet.keys()]) {
      if (present.has(id)) continue

      fleet.delete(id)
      changed = true
    }

    if (changed) publish()
  }

  //  Asked for after the subscription is open, never before. A frame published between the two calls
  //  is lost in the other order, and the vehicle it belonged to sits at a stale position until its
  //  next one -- a quarter second at the rate the aircraft reports, and indefinitely for a vehicle
  //  that has just stopped reporting. The second case is the one that matters, and it does not
  //  depend on the rate. This way the gap does not exist.
  //
  //  Repeated on every reopen as well as at startup, because that is precisely when a snapshot is
  //  worth most: the fleet moved while the console was disconnected, and one request corrects every
  //  vehicle at once instead of waiting for each to report itself.
  const seed = () => {
    const request = new AbortController()
    snapshotRequest = request

    fetch(SNAPSHOT_PATH, { signal: request.signal })
      .then((response) => {
        if (!response.ok) {
          throw new Error(`${SNAPSHOT_PATH} responded ${response.status} ${response.statusText}.`)
        }

        return response.json() as Promise<VehicleFrame[]>
      })
      .then((frames) => {
        //  One publish for the whole snapshot rather than one per vehicle: the seed is a single
        //  event in the operator's terms, and a full fleet would otherwise redraw the layer twelve
        //  times.
        let changed = false
        for (const frame of frames) changed = admit(frame) || changed

        if (changed) publish()
      })
      .catch((error: unknown) => {
        //  An aborted fetch is this client being disposed, not a fault.
        if (request.signal.aborted) return

        //  Not fatal: the stream still fills the map in, one vehicle at a time, as each reports.
        console.warn('Telemetry snapshot failed; the map will fill in from the stream.', error)
      })
  }

  const open = () => {
    if (disposed) return

    const opened = new EventSource(STREAM_PATH)
    stream = opened

    //  Restarted by anything at all arriving on the stream. What it is watching for is a connection
    //  that is open at the socket and dead above it: kill the station behind a proxy and the proxy
    //  can hold the response open with nothing on the other end of it, so the browser reports a
    //  healthy stream, fires no error, and never retries. Measured, not assumed -- with the dev
    //  server in front, a stopped API produced no error event in 33 seconds, and restarting it left
    //  the console frozen on the last frame with no way back but a reload. That is the console
    //  showing a picture it has no reason to believe is current, which is the one thing the station
    //  is built not to do (HAZ-01), so silence has to be a fault rather than an absence of news.
    const heardFromStation = () => {
      clearTimeout(silenceTimer)
      silenceTimer = setTimeout(() => {
        console.warn(
          `Telemetry stream ${STREAM_PATH} silent for ${SILENCE_TIMEOUT_MS} ms; reopening.`,
        )

        //  Closed explicitly before reopening. The connection this replaces is, as far as the
        //  browser is concerned, perfectly healthy, so nothing else will ever tidy it away.
        opened.close()
        open()
      }, SILENCE_TIMEOUT_MS)
    }

    heardFromStation()

    //  Both events carry an array of vehicles -- one element for a report, the whole fleet for a
    //  tick -- so the payload is parsed the same way and only what is done with it differs.
    //  The DOM's EventSource typings only know about `message`, so a named event arrives as the
    //  base Event type and the payload has to be reclaimed here.
    const vehiclesIn = (event: Event) =>
      JSON.parse((event as MessageEvent<string>).data) as VehicleFrame[]

    opened.addEventListener(TELEMETRY_EVENT_TYPE, (event) => {
      heardFromStation()

      let changed = false
      for (const frame of vehiclesIn(event)) changed = admit(frame) || changed

      if (changed) publish()
    })

    opened.addEventListener(FLEET_EVENT_TYPE, (event) => {
      heardFromStation()

      replaceFleet(vehiclesIn(event))
    })

    opened.onerror = () => {
      //  Still CONNECTING: the browser dropped an established stream and is already retrying it on
      //  its own schedule. Reopening here would race that retry and end up with two live streams, so
      //  this branch only says so and leaves it alone.
      if (opened.readyState !== EventSource.CLOSED) {
        console.warn(`Telemetry stream ${STREAM_PATH} dropped; reconnecting.`)
        return
      }

      //  CLOSED is the case EventSource does not recover from, and it is the ordinary one here rather
      //  than an edge: the spec only retries a *network* failure, and gives up permanently when a
      //  connection attempt is answered with an HTTP error or a non-SSE content type. A station whose
      //  API is restarting is answered by whatever proxy sits in front of it -- the dev server now, a
      //  502 from nginx in the deployed stack -- so without this the console goes quiet for good and
      //  only a page reload brings it back. Measured, not assumed: stopping the API leaves readyState
      //  at 2 and no further attempts.
      console.warn(`Telemetry stream ${STREAM_PATH} closed; reopening in ${REOPEN_DELAY_MS} ms.`)

      //  The watchdog would otherwise fire during the wait and open a second stream alongside this
      //  one's.
      clearTimeout(silenceTimer)
      reopenTimer = setTimeout(open, REOPEN_DELAY_MS)
    }

    seed()
  }

  open()

  return () => {
    //  Set before anything is torn down, so a reopen already queued cannot outlive the disposer.
    disposed = true

    clearTimeout(reopenTimer)
    clearTimeout(silenceTimer)
    snapshotRequest?.abort()
    stream?.close()
  }
}
