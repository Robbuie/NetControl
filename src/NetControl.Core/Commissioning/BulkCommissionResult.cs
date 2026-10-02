using System.Globalization;
using System.Text;

namespace NetControl.Core.Commissioning;

/// <summary>
/// What a bulk run did, device by device, in the order it was given them. Devices after a stop are
/// listed in <see cref="NotStarted"/> rather than missing, so "how far did it get" has an answer.
/// </summary>
public sealed record BulkCommissionResult(
    IReadOnlyList<(StaticIpRequest Request, CommissionResult Result)> Completed,
    IReadOnlyList<StaticIpRequest> NotStarted,
    bool WasStopped)
{
    public int Verified => Completed.Count(c => c.Result.IsVerified);

    public int NotVerified => Completed.Count - Verified;

    /// <summary>Devices that were written to and not verified - the ones somebody has to go and look at.</summary>
    public int WrittenButNotVerified => Completed.Count(c => !c.Result.IsVerified && c.Result.WroteToDevice);

    public bool AllVerified => NotStarted.Count == 0 && Completed.Count > 0 && NotVerified == 0;

    /// <summary>One sentence, for the record and the status line.</summary>
    public string Summary
    {
        get
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"Set static on {Completed.Count} device(s): {Verified} verified");

            if (NotVerified > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $", {NotVerified} not");

                if (WrittenButNotVerified > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $" ({WrittenButNotVerified} written to and unconfirmed)");
                }
            }

            text.Append('.');

            if (WasStopped && NotStarted.Count > 0)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $" Stopped on request with {NotStarted.Count} device(s) not started - nothing was sent to them.");
            }

            return text.ToString();
        }
    }
}
