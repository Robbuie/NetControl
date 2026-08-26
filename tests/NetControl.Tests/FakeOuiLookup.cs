using NetControl.Core;
using NetControl.Core.Oui;

namespace NetControl.Tests;

/// <summary>
/// One vendor name for every MAC, or none at all. A test about what the tool does with a vendor
/// should not depend on what the IEEE registry currently says about a particular block - the
/// registry moves, which is the same reason the plan stores the name rather than resolving it.
/// </summary>
internal sealed class FakeOuiLookup(string? vendor = "Rockwell Automation") : IOuiLookup
{
    public string? Lookup(MacAddress mac) => vendor;

    public static FakeOuiLookup Unknown() => new(null);
}
