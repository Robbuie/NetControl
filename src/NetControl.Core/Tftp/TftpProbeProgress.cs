namespace NetControl.Core.Tftp;

/// <summary>Where a running probe has got to.</summary>
/// <param name="Stage">"Asking", "Writing" or "Reading back".</param>
/// <param name="Done">Bytes moved in this stage so far.</param>
/// <param name="Total">Bytes this stage will move.</param>
public sealed record TftpProbeProgress(string Stage, long Done, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}
