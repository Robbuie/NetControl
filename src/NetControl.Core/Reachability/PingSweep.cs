using System.Diagnostics;
using System.Net;

namespace NetControl.Core.Reachability;

/// <summary>
/// Pings a list of addresses: every planned device, or the one somebody selected.
///
/// <para><b>Every target is checked before the first packet.</b> One broadcast address in the list
/// refuses the whole sweep with nothing sent, rather than skipping it: a planned address that is
/// the subnet's broadcast is a mistake in the plan, and finding out by having every device on the
/// segment answer is the wrong way to learn it.</para>
///
/// <para><b>Gentle.</b> At most <see cref="PingSweepOptions.MaxInFlight"/> at once, with a pause
/// between starts. A plan is tens of devices, not thousands, and there is nothing to gain from
/// arriving at them all in the same millisecond.</para>
///
/// <para>This answers "is something at that address". It does not say the something is the device
/// the plan meant - that is what the scan and the CIP read are for - so nothing here changes a
/// plan row's state.</para>
/// </summary>
public sealed class PingSweep(IPinger pinger, PingSweepOptions? options = null)
{
    private readonly IPinger _pinger = pinger ?? throw new ArgumentNullException(nameof(pinger));

    public PingSweepOptions Options { get; } = options ?? new PingSweepOptions();

    /// <summary>One address has its answer.</summary>
    public event EventHandler<PingProgressEventArgs>? Progress;

    /// <summary>
    /// Pings each target, in order of the list, and returns every outcome in that order. A target
    /// listed twice is pinged once.
    /// </summary>
    /// <exception cref="ReachabilityException">A target is not one host. Nothing was sent.</exception>
    public async Task<PingSweepResult> RunAsync(
        IReadOnlyList<PingTarget> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        List<IPAddress> addresses = CheckAll(targets);
        var outcomes = new PingOutcome[addresses.Count];
        int completed = 0;
        long started = Stopwatch.GetTimestamp();

        // Not disposed, deliberately. A cancelled sweep leaves this method while pings it already
        // started are still finishing, and each of those releases its slot on the way out - into a
        // disposed semaphore, if this were a using. Nothing here touches its wait handle, which is
        // the only part of a SemaphoreSlim that needs disposing.
        var slots = new SemaphoreSlim(Math.Max(1, Options.MaxInFlight));
        var running = new List<Task>(addresses.Count);

        for (int i = 0; i < addresses.Count; i++)
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            int index = i;
            running.Add(PingOneAsync(addresses[index], cancellationToken).ContinueWith(
                task =>
                {
                    slots.Release();

                    if (task.IsCompletedSuccessfully)
                    {
                        outcomes[index] = task.Result;
                        int done = Interlocked.Increment(ref completed);
                        Progress?.Invoke(this, new PingProgressEventArgs(task.Result, done, addresses.Count));
                    }

                    return task;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap());

            if (i < addresses.Count - 1 && Options.PauseBetween > TimeSpan.Zero)
            {
                await Task.Delay(Options.PauseBetween, cancellationToken).ConfigureAwait(false);
            }
        }

        // Rethrows a cancellation, or a pinger that broke its promise not to throw, after every
        // ping that was started has finished - nothing is left running against the network.
        await Task.WhenAll(running).ConfigureAwait(false);

        return new PingSweepResult(outcomes, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Checks the whole list before anything is sent, and drops duplicates.</summary>
    private static List<IPAddress> CheckAll(IReadOnlyList<PingTarget> targets)
    {
        var addresses = new List<IPAddress>(targets.Count);
        var seen = new HashSet<IPAddress>();

        foreach (PingTarget target in targets)
        {
            UnicastTarget.Ensure(target.Address, target.Mask);

            if (seen.Add(target.Address))
            {
                addresses.Add(target.Address);
            }
        }

        return addresses;
    }

    /// <summary>Up to <see cref="PingSweepOptions.Attempts"/> echo requests, stopping at a reply.</summary>
    private async Task<PingOutcome> PingOneAsync(IPAddress address, CancellationToken cancellationToken)
    {
        int attempts = Math.Max(1, Options.Attempts);
        PingOutcome outcome = null!;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            outcome = await _pinger.PingAsync(address, Options.Timeout, cancellationToken).ConfigureAwait(false);
            outcome = outcome with { Attempts = attempt };

            // A reply ends it. So does "no route": asking again will not grow one, and a second
            // attempt only doubles how long the sweep takes to say so.
            if (outcome.Status is PingStatus.Replied or PingStatus.Unreachable)
            {
                break;
            }
        }

        return outcome;
    }
}
