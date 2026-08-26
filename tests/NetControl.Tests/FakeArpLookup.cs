using System.Net;
using NetControl.Core;
using NetControl.Core.Interfaces;

namespace NetControl.Tests;

/// <summary>
/// An ARP cache a test can arrange, which the real one cannot be. The cases worth testing - an
/// address in nobody's cache, an entry learned on the wrong adapter, a table that cannot be read
/// at all - are exactly the ones you cannot produce on demand on a real machine.
/// </summary>
internal sealed class FakeArpLookup(params ArpEntry[] entries) : IArpLookup
{
    public bool IsSupported { get; init; } = true;

    public IReadOnlyList<ArpEntry> Snapshot() => entries;

    /// <summary>One ordinary dynamic entry, learned on the adapter the scan went out of.</summary>
    public static FakeArpLookup Holding(string address, MacAddress mac, int interfaceIndex = 12) =>
        new(new ArpEntry(IPAddress.Parse(address), mac, interfaceIndex, ArpEntryType.Dynamic));

    /// <summary>An ARP table that can be read and has nothing in it.</summary>
    public static FakeArpLookup Empty() => new();

    /// <summary>A machine where the table cannot be read at all - not the same as it being empty.</summary>
    public static FakeArpLookup Unavailable() => new() { IsSupported = false };
}
