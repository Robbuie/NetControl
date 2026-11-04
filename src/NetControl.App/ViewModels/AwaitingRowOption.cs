using NetControl.Core.Persistence;

namespace NetControl.App.ViewModels;

/// <summary>
/// One plan row awaiting its MAC, as the "Assign to awaiting plan row" picker lists it. A thin
/// wrapper so the picker can show and filter rows without the window reaching into the grid.
/// </summary>
public sealed class AwaitingRowOption
{
    public AwaitingRowOption(DeviceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        Row = row;
        Name = row.TryBuild(out DeviceRecord? record) && record?.DisplayName is { } name ? name : "(unnamed)";
    }

    public DeviceRowViewModel Row { get; }

    /// <summary>The same naming rule as the grid and the log - <see cref="DeviceRecord.DisplayName"/>.</summary>
    public string Name { get; }

    public string Ip => Row.IpText;

    public string Where => Row.PanelRef;

    /// <summary>Case-insensitive match on the name, the address or where it is. A blank filter matches all.</summary>
    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        string text = filter.Trim();
        return Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Ip.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Where.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
