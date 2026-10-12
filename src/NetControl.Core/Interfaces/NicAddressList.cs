using System.Collections;

namespace NetControl.Core.Interfaces;

/// <summary>
/// The IPv4 addresses an adapter holds beyond its first one, each with its mask.
///
/// <para>Its own type rather than a plain list because it lives on <see cref="NicInfo"/>, which is a
/// record compared by value: <see cref="NicMonitor"/> decides whether the adapters changed by
/// comparing two inventories, and a list compared by reference would make every poll look like a
/// change. This compares by contents.</para>
/// </summary>
public sealed class NicAddressList : IReadOnlyList<Ipv4Subnet>, IEquatable<NicAddressList>
{
    private readonly Ipv4Subnet[] _items;

    public NicAddressList(IEnumerable<Ipv4Subnet> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    public static NicAddressList Empty { get; } = new([]);

    public int Count => _items.Length;

    public Ipv4Subnet this[int index] => _items[index];

    public IEnumerator<Ipv4Subnet> GetEnumerator() => ((IEnumerable<Ipv4Subnet>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(NicAddressList? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);

    public override bool Equals(object? obj) => Equals(obj as NicAddressList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (Ipv4Subnet item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _items.Select(i => $"{i.Address}/{i.PrefixLength}"));
}
