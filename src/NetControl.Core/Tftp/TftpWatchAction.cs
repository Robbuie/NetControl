namespace NetControl.Core.Tftp;

/// <summary>
/// What the watch server did about one request. Carried on the event beside the reason, because
/// "nothing happened" and "it arrived on an adapter you did not select" are different sentences
/// and the log has to be able to tell them apart.
/// </summary>
public enum TftpWatchAction
{
    /// <summary>
    /// Recorded, and answered with a TFTP error so the far end stops and says so. What Observe
    /// mode does with a request on the adapter it is watching.
    /// </summary>
    Refused = 0,

    /// <summary>
    /// Recorded, and nothing was transmitted.
    ///
    /// <para>Two things produce this: a request that arrived on an adapter outside the filter -
    /// refusing there would mean transmitting onto a segment nobody selected - and a caller that
    /// turned refusals off deliberately.</para>
    /// </summary>
    Ignored = 1,

    /// <summary>Recorded, a refusal was attempted, and the send failed. The fault carries why.</summary>
    SendFailed = 2,
}
