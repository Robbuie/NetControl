namespace NetControl.Core.Oui;

/// <summary>
/// Turns a MAC into a manufacturer name.
///
/// This is an interface for the same reason <c>INicInventory</c> is: the real implementation
/// carries a few hundred kilobytes of embedded IEEE data, and a test that wants to check "the
/// importer stamps the vendor it was told" should not have to care what the registry currently
/// says about a particular block. The registry also moves - a vendor gets acquired, a block gets
/// transferred - which is exactly why <c>DeviceRecord.Vendor</c> stores the resolved name at
/// import time instead of resolving it on every read.
/// </summary>
public interface IOuiLookup
{
    /// <summary>
    /// The registered manufacturer for this address, or null if there is nothing useful to say.
    ///
    /// Null covers three different situations that the caller should treat identically: the
    /// address is locally administered or otherwise not a real assignment, the prefix is not in
    /// this copy of the registry, or the registry is empty. In all three the honest answer is
    /// "unknown", and inventing a placeholder string here would put that placeholder in the
    /// database.
    /// </summary>
    string? Lookup(MacAddress mac);
}
