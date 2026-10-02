namespace NetControl.Core.Commissioning;

/// <summary>
/// One device in a bulk run is about to start, or has finished. <see cref="Result"/> is null on the
/// way in. Raised on whatever thread the run is on; a UI marshals.
/// </summary>
public sealed class BulkCommissionEventArgs(int index, int total, StaticIpRequest request, CommissionResult? result = null)
    : EventArgs
{
    /// <summary>Position in the list the run was given, from 0. The caller maps it back to its own row.</summary>
    public int Index { get; } = index;

    public int Total { get; } = total;

    public StaticIpRequest Request { get; } = request;

    public CommissionResult? Result { get; } = result;
}
