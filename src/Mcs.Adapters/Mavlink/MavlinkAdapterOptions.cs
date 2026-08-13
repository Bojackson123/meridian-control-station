using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;

namespace Mcs.Adapters.Mavlink;

/// <summary>
/// The <c>Adapters:Mavlink</c> configuration section: where <see cref="MavlinkUdpAdapter"/> listens
/// for a vehicle's telemetry.
/// </summary>
/// <remarks>
/// <b>The whole section exists to be got wrong loudly.</b> A UDP socket bound to the wrong address
/// behaves identically to one nothing is sending to -- it binds, it reports no error, and it
/// receives nothing forever -- so a typo here is a station that looks healthy and shows an empty
/// map. That is why the address is parsed during validation rather than at bind time, and why the
/// message names the setting.
/// <para>
/// Mutable properties because that is what the configuration binder needs, matching
/// <c>FakeFeedOptions</c>. There is deliberately no <c>Enabled</c> flag: an adapter that is
/// configured but silently not running is the same failure this section is arranged to prevent, and
/// which adapters exist is a question for the host's registrations, where it can be read.
/// </para>
/// </remarks>
public sealed class MavlinkAdapterOptions : IValidatableObject
{
    /// <summary>The configuration section this binds to.</summary>
    /// <remarks>
    /// Nested under <c>Adapters</c> rather than sitting at the root, because M3 adds a second
    /// adapter with its own transport settings and two sibling sections read better than two
    /// top-level ones. In the environment this is <c>Adapters__Mavlink__Port</c>.
    /// </remarks>
    public const string SectionName = "Adapters:Mavlink";

    /// <summary>
    /// Gets or sets the local address to bind. Defaults to every interface, which is what a
    /// container needs -- the simulator sends from another container, and a loopback bind would
    /// receive none of it.
    /// </summary>
    /// <remarks>
    /// <b>That default accepts datagrams from anyone who can reach the port</b>, and nothing on
    /// this link authenticates a sender -- signing is not implemented and is not planned. The
    /// consequence is worth stating rather than leaving to be discovered: the store admits twelve
    /// vehicles and never reclaims a slot unaided, so a sender within reach can fill the fleet with
    /// invented system ids and every genuine vehicle is refused from then on. Narrowing this to one
    /// interface is the mitigation available today; the README's limitations say so, and auth on the
    /// link is a later milestone's work rather than something to approximate here.
    /// </remarks>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Gets or sets the local UDP port to bind. Defaults to 14550, the port ground stations
    /// conventionally listen on, so a stock autopilot or simulator configuration reaches this
    /// station without being told where it is.
    /// </summary>
    /// <remarks>
    /// Zero is permitted and means "any free port", which is not useful in a deployment and is what
    /// lets a test -- or two stations in one process -- bind without arranging port numbers between
    /// them. <see cref="MavlinkUdpAdapter.LocalEndPoint"/> reports what was actually bound.
    /// </remarks>
    [Range(0, 65535)]
    public int Port { get; set; } = 14550;

    /// <summary>
    /// Builds the endpoint to bind. Valid only after validation has passed; the host validates this
    /// section on startup, so a caller reaching here has already been through it.
    /// </summary>
    internal IPEndPoint ResolveEndPoint() => new(IPAddress.Parse(ListenAddress), Port);

    /// <inheritdoc />
    /// <remarks>
    /// Parsing, not a regular expression. The address forms that are actually bindable are what
    /// <see cref="IPAddress.TryParse(string, out IPAddress)"/> accepts, and a pattern approximating
    /// that set would differ from it in exactly the cases worth catching. A host name is rejected
    /// along with everything else unparseable: this is a local address to bind, not a peer to
    /// resolve, and resolving one would let a name with two records bind an interface nobody chose.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IPAddress.TryParse(ListenAddress, out _))
        {
            yield return new ValidationResult(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' is not an IP address. Set {1}:{2} to a local address to bind -- "
                    + "0.0.0.0 for every interface, or 127.0.0.1 to accept only traffic from this "
                    + "machine.",
                    ListenAddress,
                    SectionName,
                    nameof(ListenAddress)),
                [nameof(ListenAddress)]);
        }
    }
}
