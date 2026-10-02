using System.Net;
using NetControl.Core.Reachability;

namespace NetControl.Tests;

/// <summary>
/// Answers pings from a script, so the sweep and the view models are tested without ICMP - and
/// without a test ever pinging anything.
///
/// <para>Each address gets a queue of statuses, one per echo request; once the queue is empty, or
/// for an address with no script, it times out. It also tracks how many requests were in flight at
/// once, which is how "gentle" is tested rather than asserted about.</para>
/// </summary>
internal sealed class FakePinger : IPinger
{
    private readonly Dictionary<IPAddress, Queue<PingStatus>> _script = [];
    private readonly Lock _gate = new();
    private int _inFlight;

    /// <summary>Every address asked about, once per echo request, in the order asked.</summary>
    public List<IPAddress> Asked { get; } = [];

    public int MaxConcurrent { get; private set; }

    /// <summary>How long each echo takes. A little, so concurrency can actually overlap.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(15);

    public FakePinger Answers(string address, params PingStatus[] statuses)
    {
        _script[IPAddress.Parse(address)] = new Queue<PingStatus>(statuses);
        return this;
    }

    public async Task<PingOutcome> PingAsync(
        IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        PingStatus status;

        lock (_gate)
        {
            Asked.Add(address);
            _inFlight++;
            MaxConcurrent = Math.Max(MaxConcurrent, _inFlight);

            status = _script.TryGetValue(address, out Queue<PingStatus>? queue) && queue.Count > 0
                ? queue.Dequeue()
                : PingStatus.TimedOut;
        }

        try
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }

        return new PingOutcome
        {
            Address = address,
            Status = status,
            RoundTrip = status == PingStatus.Replied ? TimeSpan.FromMilliseconds(2) : null,
        };
    }
}
