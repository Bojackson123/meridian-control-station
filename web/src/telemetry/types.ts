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
 * This is not staleness. Staleness is the console's own judgement, derived from `receivedAtUtc` on
 * every render; a vehicle reports `Healthy` in the last frame before the link dies, and the console
 * must still call it stale seconds later. Never derive one from the other.
 */
export type LinkStatus = 'Healthy' | 'Degraded' | 'Lost'

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
 * One vehicle's latest frame: what the vehicle claimed, plus the one thing it did not -- the time
 * the station observed it.
 *
 * `receivedAtUtc` is the station's clock reading at arrival, not the vehicle's. Everything the
 * console will ever say about how current a picture is has to come from this field (MCS-005).
 */
export interface VehicleFrame {
  vehicleId: string
  latitudeDegrees: number
  longitudeDegrees: number
  altitude: Altitude
  groundSpeedMetersPerSecond: number
  headingDegrees: number

  //  Nullable on the wire: a vehicle that does not report a battery says nothing rather than zero.
  batteryPercent: number | null

  linkStatus: LinkStatus
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
