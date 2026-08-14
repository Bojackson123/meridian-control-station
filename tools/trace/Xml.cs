using System.Xml;
using System.Xml.Linq;

namespace Mcs.Trace;

/// <summary>Loads xml, and names the file when it will not load.</summary>
/// <remarks>
///   <para>
///     <see cref="XDocument.Load(string)"/> throws <see cref="XmlException"/>, which reaches the
///     top of this program as a stack trace reporting a line and column in a file it does not
///     name. That is the wrong failure for a tool whose entire output is named, actionable
///     messages.
///   </para>
///   <para>
///     And it is not a rare one. The evidence directory is merged from two CI jobs and everything
///     in it was written by a process that can be cancelled: a <c>dotnet test</c> interrupted
///     part-way leaves a truncated <c>.trx</c> behind, and a truncated file is a malformed one.
///     Skipping it quietly is the alternative and is worse -- a suite's results would vanish and
///     the requirements it covers would fail as "no test run reported it at all", which sends
///     whoever reads that to write a test that already exists.
///   </para>
/// </remarks>
internal static class Xml
{
    /// <param name="what">
    ///   What this program wanted the file for, in a phrase that finishes "read as ...". It is the
    ///   difference between knowing a file is corrupt and knowing why anything was reading it.
    /// </param>
    internal static XDocument Load(string path, string what)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (XmlException ex)
        {
            //  FormatException because that is already this program's word for "an input is not
            //  what it claims to be", and it is already caught and printed without a stack trace.
            throw new FormatException($"'{path}', read as {what}, is not well-formed xml: {ex.Message}", ex);
        }
    }
}
