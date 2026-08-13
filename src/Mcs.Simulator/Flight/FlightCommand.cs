namespace Mcs.Simulator.Flight;

/// <summary>
/// What the follower is asking the aircraft for: a heading to point at and an altitude to hold.
/// </summary>
/// <remarks>
/// The whole interface between guidance and kinematics, and it is two numbers on purpose. Guidance
/// decides <i>where to go</i>; the envelope decides <i>how fast it may get there</i>, and neither
/// half can quietly take the other's decision if the only thing that crosses between them is a
/// target the kinematics is free to fall short of.
/// <para>
/// There is no commanded speed. The aircraft holds its cruise speed all the way round, which is
/// what makes <c>speed x dt</c> agree with the distance between two consecutive positions without
/// anything having to keep the two in step.
/// </para>
/// </remarks>
/// <param name="HeadingDegrees">Where to point, clockwise from true north.</param>
/// <param name="AltitudeMetersMsl">What altitude to hold, in metres above mean sea level.</param>
internal readonly record struct FlightCommand(double HeadingDegrees, double AltitudeMetersMsl);
