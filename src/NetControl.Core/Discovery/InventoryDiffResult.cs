using System.Globalization;
using System.Text;
using NetControl.Core.Persistence;

namespace NetControl.Core.Discovery;

/// <summary>Everything that changed since the earlier scan, worst first, and how much did not.</summary>
public sealed record InventoryDiffResult(
    IReadOnlyList<InventoryChange> Changes, int Unchanged, DateTimeOffset PreviousUtc)
{
    public bool HasChanges => Changes.Count > 0;

    public EventSeverity Severity =>
        Changes.Count == 0 ? EventSeverity.Info : Changes.Max(change => change.Severity);

    public int Count(InventoryChangeKind kind) => Changes.Count(change => change.Kind == kind);

    /// <summary>One sentence for the record and for the heading over the list.</summary>
    public string Summary
    {
        get
        {
            string since = PreviousUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

            if (Changes.Count == 0)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"No change since the scan of this subnet at {since}: the same {Unchanged} device(s) answered.");
            }

            var parts = new List<string>();
            Add(parts, Count(InventoryChangeKind.Moved), "moved");
            Add(parts, Count(InventoryChangeKind.DifferentDevice), "address(es) now held by a different device");
            Add(parts, Count(InventoryChangeKind.Replaced), "replaced");
            Add(parts, Count(InventoryChangeKind.FirmwareChanged), "with new firmware");
            Add(parts, Count(InventoryChangeKind.New), "new");
            Add(parts, Count(InventoryChangeKind.NotAnswering), "not answering");

            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"Since the scan of this subnet at {since}: ");
            text.Append(string.Join(", ", parts));
            text.Append(CultureInfo.InvariantCulture, $"; {Unchanged} unchanged.");
            return text.ToString();
        }
    }

    private static void Add(List<string> parts, int count, string what)
    {
        if (count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {what}"));
        }
    }
}
