using NetControl.Core;

namespace NetControl.App.Serving;

/// <summary>
/// "Is this MAC in the plan, and what did we call it?" - asked by the request log so that a device
/// somebody typed in reads differently from a stranger, and reads by name rather than by MAC.
///
/// <para>It is a separate question from <c>AssignmentDecision.ShouldServe</c>, and the difference
/// is the whole reason this exists. A planned device whose adapter has gone down produces
/// <c>ShouldServe == false</c> with a perfectly good explanation, and colouring that row the same
/// as a stranger on the network would be wrong: one is a device you are waiting for, the other is
/// a device nobody typed in.</para>
///
/// <para><b>The plan, not the servable subset.</b> This deliberately covers every row in the file,
/// including one with a MAC and no address yet - which is exactly what "Add to plan" from a log
/// row produces, and is a normal state for a device planned at 7am from a label. Backing this with
/// the serving policy instead would answer "no" for that device and file it as a stranger while
/// the user was still typing its address, which is the one moment they are certainly watching
/// the log.</para>
/// </summary>
public interface IPlanIndex
{
    /// <summary>The planned device, or null when the plan has no row for this MAC.</summary>
    PlannedDevice? Find(MacAddress mac);
}
