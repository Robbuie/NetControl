namespace NetControl.Core.Commissioning;

/// <summary>
/// Runs the ordinary commissioning sequence over a list of devices, one at a time.
///
/// <para><b>It is the single-device sequence, repeated - nothing more.</b> Each request is still one
/// device at its own address, unicast; each still reads capability first, writes attribute 3 before
/// attribute 5 and reads back before calling anything done. The commissioner's progress events still
/// fire for every step, so the record reads as a sequence of single operations, which is what it is.
/// There is no broadcast here and no "configure everything you found": the list is the plan, and the
/// caller asks a person to confirm it before calling this.</para>
///
/// <para><b>One at a time, deliberately.</b> Industrial stacks are thin, and a record of two writes
/// interleaved is a record nobody can read afterwards. A plan of forty devices takes a few minutes
/// this way, which is a good trade for an account of each one that stands on its own.</para>
///
/// <para><b>A stop takes effect between devices, never during one.</b> Cancelling half way through a
/// device can leave it with attribute 3 written and attribute 5 not - Static, at whatever address it
/// had - which is the worst place to stop. <see cref="RequestStop"/> lets the device in progress
/// finish and starts no more. The cancellation token is still honoured for the case where the
/// window is closing and there is no choice.</para>
///
/// <para>A device that fails does not stop the run. Each failure is that device's own, worded by the
/// commissioner, and the next device on the list has nothing to do with it.</para>
/// </summary>
public sealed class BulkCommissioner(StaticIpCommissioner commissioner)
{
    private readonly StaticIpCommissioner _commissioner =
        commissioner ?? throw new ArgumentNullException(nameof(commissioner));

    private volatile bool _stopRequested;

    /// <summary>The device at <see cref="BulkCommissionEventArgs.Index"/> is about to be contacted.</summary>
    public event EventHandler<BulkCommissionEventArgs>? DeviceStarting;

    /// <summary>The device has a result.</summary>
    public event EventHandler<BulkCommissionEventArgs>? DeviceFinished;

    public bool IsStopRequested => _stopRequested;

    /// <summary>Asks the run to stop after the device in progress. Safe from any thread.</summary>
    public void RequestStop() => _stopRequested = true;

    public async Task<BulkCommissionResult> RunAsync(
        IReadOnlyList<StaticIpRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        _stopRequested = false;
        var completed = new List<(StaticIpRequest Request, CommissionResult Result)>(requests.Count);

        for (int i = 0; i < requests.Count; i++)
        {
            if (_stopRequested)
            {
                return new BulkCommissionResult(completed, [.. requests.Skip(i)], WasStopped: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            StaticIpRequest request = requests[i];
            DeviceStarting?.Invoke(this, new BulkCommissionEventArgs(i, requests.Count, request));

            // Deliberately not ConfigureAwait(false). The run resumes on the caller's context between
            // devices, so DeviceStarting and DeviceFinished arrive where the caller is - on the UI
            // thread, for the grid - and a row can be updated from them without marshalling. The
            // socket work inside the commissioner still runs off that thread; only the loop does not.
            CommissionResult result = await _commissioner.RunAsync(request, cancellationToken);
            completed.Add((request, result));

            DeviceFinished?.Invoke(this, new BulkCommissionEventArgs(i, requests.Count, request, result));
        }

        return new BulkCommissionResult(completed, [], WasStopped: false);
    }
}
