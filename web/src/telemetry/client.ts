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

/** The named SSE event carrying a frame. Mirrors `TelemetryEndpoints.TelemetryEventType`. */
const TELEMETRY_EVENT_TYPE = 'telemetry'

/** The event the station sends to prove it is still there. Mirrors `TelemetryEndpoints.HeartbeatEventType`. */
const HEARTBEAT_EVENT_TYPE = 'heartbeat'

/**
 * How long the stream may say nothing at all -- no frame, no heartbeat -- before it is treated as
 * dead and reopened.
 *
 * Comfortably more than two of the station's 15-second heartbeat periods, so a single late one under
 * load is not mistaken for an outage.
 */
const SILENCE_TIMEOUT_MS = 40_000

/**
 * How long to wait before reopening a stream the browser has given up on. Matches the retry interval
 * `EventSource` uses for the drops it does handle itself, so an outage recovers at one cadence rather
 * than two.
 */
const REOPEN_DELAY_MS = 3_000

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
 * Note what this does not do: while the station is unreachable, the last frames stay on the map at
 * their last positions. Showing the operator that the picture has stopped being current is MCS-002's
 * job and needs a designed visual language rather than an improvised one, so for now the disconnect
 * is visible in the browser console and nowhere else.
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
  const admit = (frame: VehicleFrame): boolean => {
    const held = fleet.get(frame.vehicleId)
    if (held && Date.parse(held.receivedAtUtc) >= Date.parse(frame.receivedAtUtc)) return false

    fleet.set(frame.vehicleId, frame)
    return true
  }

  //  Asked for after the subscription is open, never before. A frame published between the two calls
  //  is lost in the other order, and the vehicle it belonged to sits at a stale position until its
  //  next one -- a whole second at the feed's rate, and indefinitely for a vehicle that has just
  //  stopped reporting. This way the gap does not exist.
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

    opened.addEventListener(TELEMETRY_EVENT_TYPE, (event) => {
      heardFromStation()

      //  The DOM's EventSource typings only know about `message`, so a named event arrives as the
      //  base Event type and the payload has to be reclaimed here.
      const frame = JSON.parse((event as MessageEvent<string>).data) as VehicleFrame

      if (admit(frame)) publish()
    })

    //  The heartbeat carries no data and is listened for only so that it counts as news. A quiet
    //  fleet is indistinguishable from a dead station without it, which is exactly why the station
    //  sends one.
    opened.addEventListener(HEARTBEAT_EVENT_TYPE, heardFromStation)

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
