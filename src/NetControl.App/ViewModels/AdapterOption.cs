using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core.Interfaces;

namespace NetControl.App.ViewModels;

/// <summary>
/// One row in the adapter picker, identified by interface index for the whole session.
///
/// <para>The identity is the point. A commissioning session routinely involves unplugging a USB
/// adapter, or a VPN coming up and going down, and Windows removes and re-adds the adapter around
/// those events. If the picker rebuilt its list each time, the user's selection would be silently
/// dropped and the tool would carry on listening to nothing. Instead the option survives its
/// adapter: <see cref="Nic"/> goes null while it is away and comes back when it returns, so
/// replugging a cable restores a working selection with no restart and no click.</para>
/// </summary>
public sealed partial class AdapterOption : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresent))]
    [NotifyPropertyChangedFor(nameof(Name))]
    [NotifyPropertyChangedFor(nameof(Description))]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(AddressText))]
    [NotifyPropertyChangedFor(nameof(LinkText))]
    [NotifyPropertyChangedFor(nameof(CanServe))]
    [NotifyPropertyChangedFor(nameof(IsLikelyVirtual))]
    private NicInfo? _nic;

    public AdapterOption(NicInfo nic)
    {
        ArgumentNullException.ThrowIfNull(nic);

        Index = nic.Index;
        _nic = nic;

        // Kept from the last time we saw it, so a removed adapter is still named in the picker
        // rather than becoming "interface 18".
        LastKnownName = nic.Name;
        LastKnownDescription = nic.Description;
    }

    public int Index { get; }

    /// <summary>The adapter is in the current inventory. False means unplugged or disabled.</summary>
    public bool IsPresent => Nic is not null;

    public string LastKnownName { get; private set; }

    public string LastKnownDescription { get; private set; }

    public string Name => Nic?.Name ?? LastKnownName;

    public string Description => Nic?.Description ?? LastKnownDescription;

    public string DisplayName => $"[{Index}] {Name}";

    /// <summary>
    /// Only a presentation hint - it never decides anything. See <see cref="NicInfo.IsLikelyVirtual"/>.
    /// </summary>
    public bool IsLikelyVirtual => Nic?.IsLikelyVirtual ?? false;

    public bool CanServe => Nic?.CanServe ?? false;

    public string AddressText
    {
        get
        {
            if (Nic is not { } nic)
            {
                return "not present";
            }

            if (nic.IPv4 is not { } address)
            {
                return "no IPv4 address";
            }

            return nic.PrefixLength is { } prefix
                ? string.Create(CultureInfo.InvariantCulture, $"{address}/{prefix}")
                : address.ToString();
        }
    }

    public string LinkText => Nic is { } nic
        ? nic.Status.ToString()
        : "removed";

    /// <summary>
    /// Refreshes from a new inventory snapshot. Null means the adapter is no longer there, which
    /// is remembered rather than acted on - the user's choice is not ours to discard.
    /// </summary>
    public void Update(NicInfo? nic)
    {
        if (nic is not null)
        {
            LastKnownName = nic.Name;
            LastKnownDescription = nic.Description;
        }

        Nic = nic;
    }

    public override string ToString() => DisplayName;
}
