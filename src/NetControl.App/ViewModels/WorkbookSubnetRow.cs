namespace NetControl.App.ViewModels;

/// <summary>
/// One subnet of a workbook sheet in the import dialog: the mask and gateway every row on it gets.
/// Plain settable properties, because the dialog's text boxes write straight into them and nothing
/// else watches them.
/// </summary>
public sealed class WorkbookSubnetRow
{
    public WorkbookSubnetRow(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Prefix = prefix;
    }

    /// <summary>The first three octets, e.g. 192.168.1 - the key <c>PlanSheet.Subnets</c> uses.</summary>
    public string Prefix { get; }

    /// <summary>"192.168.1.x", as the subnet is labelled on screen.</summary>
    public string Label => Prefix + ".x";

    public string Mask { get; set; } = "255.255.255.0";

    public string Gateway { get; set; } = string.Empty;
}
