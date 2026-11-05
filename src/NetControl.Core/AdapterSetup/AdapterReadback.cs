namespace NetControl.Core.AdapterSetup;

/// <summary>
/// Whether the adapter came up as asked, read back off Windows - the same rule Set static follows for
/// a device: netsh saying it worked is netsh saying it worked, and only the adapter's own state says
/// the change took.
/// </summary>
public static class AdapterReadback
{
    /// <summary>Null when the adapter now matches the change, or why it does not.</summary>
    public static string? Mismatch(AdapterIpMode mode, AdapterIpSettings? settings, AdapterSnapshot? now)
    {
        if (now is null)
        {
            return "The adapter could not be read back - it may have been unplugged or disabled.";
        }

        switch (mode)
        {
            case AdapterIpMode.Dhcp:
                return now.IsDhcp ? null : $"{now.Name} is still static: {now.Summary}.";

            case AdapterIpMode.Replace:
                ArgumentNullException.ThrowIfNull(settings);
                if (now.IsDhcp)
                {
                    return $"{now.Name} is still on DHCP: {now.Summary}.";
                }

                return now.Addresses.Count > 0 && now.Addresses[0].Ip.Equals(settings.Ip) && now.Addresses[0].Mask.Equals(settings.Mask)
                    ? null
                    : $"{now.Name} does not have {settings.Ip}/{settings.Mask} yet: {now.Summary}.";

            default:
                ArgumentNullException.ThrowIfNull(settings);
                return now.Addresses.Any(a => a.Ip.Equals(settings.Ip) && a.Mask.Equals(settings.Mask))
                    ? null
                    : $"{now.Name} does not have {settings.Ip}/{settings.Mask} yet: {now.Summary}.";
        }
    }

    /// <summary>Null when the adapter is back as the snapshot had it, or what still differs.</summary>
    public static string? Restored(AdapterSnapshot wanted, AdapterSnapshot? now)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        if (now is null)
        {
            return "The adapter could not be read back - it may have been unplugged or disabled.";
        }

        if (wanted.IsDhcp)
        {
            return now.IsDhcp ? null : $"{now.Name} is still static: {now.Summary}.";
        }

        bool same = !now.IsDhcp
            && wanted.Addresses.All(w => now.Addresses.Any(a => a.Ip.Equals(w.Ip) && a.Mask.Equals(w.Mask)));

        return same ? null : $"{now.Name} is {now.Summary}, not {wanted.Summary}.";
    }
}
