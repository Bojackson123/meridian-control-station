/**
 * The station's telemetry as it arrives over HTTP.
 *
 * These mirror `VehicleFrameResponse` on the API side, which is a DTO precisely so that renaming a
 * domain member there is a refactor rather than a break here. The names are camelCase because that
 * is what ASP.NET Core's web defaults serialise, and the enums are member names rather than numbers
 * because the host registers a string enum converter -- a renumbered enum would otherwise change
 * what `2` means to a console written against the old numbering, silently.
 */

/**
 * The health of the radio link, as the vehicle's link layer reports it.
 *
 * This is not staleness. It is the vehicle's claim about its own radio, made in a frame that by
 * definition arrived; `VehicleState` is the station's observation of silence. A vehicle reports
 * `Healthy` in the last frame before the link dies, and the station still calls it stale seconds
 * later. Never derive one from the other.
 */
export type LinkStatus = 'Healthy' | 'Degraded' | 'Lost'

/**
 * How current the station considers a vehicle's last report to be (MCS-002).
 *
 * **Computed by the station and sent, never worked out here.** The threshold arithmetic needs a
 * clock, and the browser's is not one this console may trust: a machine thirty seconds out would
 * render a live aircraft as lost or, far worse, a lost one as live -- the failure this whole state
 * language exists to prevent, arriving through a component nobody would think to suspect. The wire
 * carries the answer; the console renders it.
 */
export type VehicleState = 'Live' | 'Stale' | 'Lost'

/** The datum an altitude was measured against. Converting between them needs terrain the station does not hold. */
export type AltitudeReference = 'Msl' | 'Agl' | 'Hae'

/**
 * An altitude and its reference, together (MCS-004).
 *
 * Nested rather than flattened into a bare `altitudeMeters`, because a consumer handed a number has
 * no way left to ask what it is above.
 */
export interface Altitude {
  meters: number
  reference: AltitudeReference
}

/**
 * One vehicle's latest frame: what the vehicle claimed, plus the things it did not -- when the
 * station observed it, and how current the station considers it now.
 *
 * `receivedAtUtc` is the station's clock reading at arrival, not the vehicle's (MCS-005). It orders
 * frames; it is not what the display's age is computed from, because that subtraction would need a
 * local clock. `state` and `ageMilliseconds` are the station's own answer, evaluated against the
 * station clock at the moment the event was written.
 */
export interface VehicleFrame {
  vehicleId: string
  latitudeDegrees: number
  longitudeDegrees: number
  altitude: Altitude

  //  Three nullable fields, and null means the vehicle did not report one -- never that it reported
  //  zero. A vehicle sends its position and its velocity in separate messages at separate rates, so
  //  the station knowing where something is without knowing which way it faces is an ordinary state
  //  and not an error. Rendering any of these as 0 would be a confident claim the data cannot
  //  support: a heading of 0 is north, and a ground speed of 0 is a vehicle at rest.
  groundSpeedMetersPerSecond: number | null
  headingDegrees: number | null
  batteryPercent: number | null

  linkStatus: LinkStatus

  state: VehicleState

  //  How long before this event was written the frame had arrived, in whole milliseconds by the
  //  station clock. Measured monotonically over there, so it does not jump when either machine's
  //  wall clock is corrected.
  ageMilliseconds: number

  receivedAtUtc: string
}

/**
 * Every vehicle the station knows about, keyed by id.
 *
 * A map rather than an array because every update is a keyed replacement of one vehicle's latest
 * frame, and readonly because consumers render it -- the only thing allowed to change it is the
 * client that owns the connection.
 */
export type Fleet = ReadonlyMap<string, VehicleFrame>
