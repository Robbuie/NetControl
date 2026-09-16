namespace NetControl.App.Diagnostics;

/// <summary>
/// One downloadable file attached to a published release, and the checksum published beside it.
/// </summary>
/// <param name="Name">The file name as published, which is also what it is saved as.</param>
/// <param name="Url">Where to fetch it. A plain download address, not an API one.</param>
/// <param name="Size">Bytes, as the release reports them. Used for the progress bar and for a
/// sanity check against what actually arrived.</param>
/// <param name="ChecksumUrl">
/// The <c>.sha256</c> published beside it.
///
/// <para><b>Nullable in the type and refused in the downloader.</b> The release workflow publishes
/// one for every artefact, so an asset without a checksum means either a hand-made release or
/// something that is not the release it claims to be - and this tool is about to run whatever it
/// downloads on a machine that writes to plant equipment. It is a nullable property so the reason
/// for the refusal can be worded exactly, rather than a required one that turns a missing file into
/// a parse failure.</para>
/// </param>
public sealed record UpdateAsset(string Name, Uri Url, long Size, Uri? ChecksumUrl)
{
    /// <summary>Roughly, for a dialog. Releases here are tens of megabytes: MB is the right unit.</summary>
    public string SizeText => Size <= 0 ? "unknown size" : $"{Size / 1024d / 1024d:N1} MB";
}
