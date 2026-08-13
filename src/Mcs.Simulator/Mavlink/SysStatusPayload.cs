using System.Buffers.Binary;

using Mcs.Simulator.Flight;

namespace Mcs.Simulator.Mavlink;

/// <summary>
/// Writes a SYS_STATUS (id 1) payload: battery state, and the sensor health bitmasks that the fault
/// work will eventually drive.
/// </summary>
/// <remarks>
/// <b>Fault flags are stubbed, and this is the whole of the stub: every sensor is present, enabled
/// and healthy, always.</b> The injection point is <see cref="HealthySensors"/> and the two
/// constants beside it -- turning a fault on means clearing a bit from the health mask and nothing
/// else. A half-built fault system was the alternative and is worse than none: it would put a
/// failure mode in front of an operator that the station has no defined response to, and the value
/// of a fault injector is entirely in the response it provokes.
///
/// <para>
/// <b>The battery percentage is a measurement and the voltage is derived from it</b>, which is the
/// opposite of a real pack and is fine here because this one is imaginary. What matters is that
/// they agree: a station that ignored the percentage and estimated from voltage must arrive at
/// roughly the same answer, or the disagreement is the simulator's fault rather than the station's.
/// </para>
///
/// <para>
/// <c>battery_remaining</c> is <c>int8</c> and -1 means unmeasured. This vehicle always has a
/// measurement, so it never sends -1 -- the station's handling of that value is exercised by the
/// committed byte vectors, where it can be asserted rather than waited for.
/// </para>
/// </remarks>
internal static class SysStatusPayload
{
    /// <summary>The full payload length SYS_STATUS declares, before v2 truncation.</summary>
    internal const int PayloadLength = 31;

    /// <summary>
    /// The <c>MAV_SYS_STATUS_SENSOR</c> bits this airframe reports: three-axis gyro, accelerometer
    /// and magnetometer, absolute pressure, GPS, attitude stabilisation, yaw position, altitude and
    /// horizontal position control, and motor outputs.
    /// </summary>
    /// <remarks>
    /// <b>This is the fault injection point.</b> Present and enabled stay as they are; a fault is a
    /// bit cleared from the health mask. Kept as one named constant used three times rather than
    /// three literals, so that "healthy" cannot drift away from "present" and leave the station
    /// reading a sensor that is enabled, unhealthy, and was never fitted.
    /// </remarks>
    private const uint HealthySensors =
        0x1u        //  MAV_SYS_STATUS_SENSOR_3D_GYRO
        | 0x2u      //  MAV_SYS_STATUS_SENSOR_3D_ACCEL
        | 0x4u      //  MAV_SYS_STATUS_SENSOR_3D_MAG
        | 0x8u      //  MAV_SYS_STATUS_SENSOR_ABSOLUTE_PRESSURE
        | 0x20u     //  MAV_SYS_STATUS_SENSOR_GPS
        | 0x400u    //  MAV_SYS_STATUS_SENSOR_ATTITUDE_STABILIZATION
        | 0x800u    //  MAV_SYS_STATUS_SENSOR_YAW_POSITION
        | 0x1000u   //  MAV_SYS_STATUS_SENSOR_Z_ALTITUDE_CONTROL
        | 0x2000u   //  MAV_SYS_STATUS_SENSOR_XY_POSITION_CONTROL
        | 0x4000u;  //  MAV_SYS_STATUS_SENSOR_MOTOR_OUTPUTS

    /// <summary>Mainloop load in tenths of a percent, so this is 25%.</summary>
    private const ushort LoadTenthsOfPercent = 250;

    /// <summary>A four-cell lithium pack at rest: 3.5 V per cell flat, 4.2 V per cell full.</summary>
    private const double FlatPackMillivolts = 14_000.0;

    private const double FullPackMillivolts = 16_800.0;

    /// <summary>Pack current in centiamps: a steady 8 A, since there is no throttle model to vary it.</summary>
    private const short CruiseCurrentCentiAmps = 800;

    /// <summary>Writes the payload.</summary>
    /// <param name="destination">Exactly <see cref="PayloadLength"/> bytes.</param>
    /// <param name="state">The aircraft's current state; the battery percentage comes from here.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is the wrong length.</exception>
    internal static void Write(Span<byte> destination, in AircraftState state)
    {
        MavlinkPayloadBuffer.EnsureLength(destination, PayloadLength, nameof(SysStatusPayload));

        //  The kinematics floors the battery at 0 and starts it at 100, so this is already in
        //  range; rounded rather than truncated so a pack at 99.6% does not report 99.
        double batteryPercent = Math.Clamp(state.BatteryPercent, 0, 100);
        sbyte batteryRemaining = (sbyte)Math.Round(batteryPercent);

        BinaryPrimitives.WriteUInt32LittleEndian(destination, HealthySensors);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], HealthySensors);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], HealthySensors);

        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], LoadTenthsOfPercent);

        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[14..],
            (ushort)Math.Round(
                FlatPackMillivolts
                + ((FullPackMillivolts - FlatPackMillivolts) * batteryPercent / 100.0)));

        BinaryPrimitives.WriteInt16LittleEndian(destination[16..], CruiseCurrentCentiAmps);

        //  drop_rate_comm, errors_comm and the four autopilot-specific error counts. Zero, and the
        //  loop below rather than six literal writes because they are one fact -- nothing has gone
        //  wrong on this vehicle's own links -- and six lines would invite one of them to drift.
        destination[18..30].Clear();

        //  The last byte, and the only signed 8-bit field in the message. Written through a cast so
        //  the sign survives: assigning -1 through the byte-typed indexer would send 255.
        destination[30] = (byte)batteryRemaining;
    }
}
