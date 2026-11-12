namespace NetControl.Core.Analysis;

/// <summary>One MAC's claim to an address: when it was first and last heard, and how.</summary>
public sealed record AddressClaim(MacAddress Mac, DateTimeOffset FirstUtc, DateTimeOffset LastUtc, AddressObservationSource LastSource, long Times);
