using NetControl.Core;

namespace NetControl.App.Serving;

/// <summary>
/// What the log needs to know about a device in the plan: that it is in there at all, and what to
/// call it.
///
/// <para>A snapshot, not a live view of the row. A log row records a moment - if renaming a device
/// in the grid retitled the requests it made ten minutes ago, the log would stop being a record of
/// what happened and become a rendering of what the plan currently says.</para>
/// </summary>
/// <param name="Mac">The MAC the plan holds.</param>
/// <param name="DisplayName">
/// From <see cref="NetControl.Core.Persistence.DeviceRecord.DisplayName"/>, so it is the same name
/// the grid shows. Null when the row has been planned but not yet labelled, which is normal for a
/// device just added from a log row.
/// </param>
public sealed record PlannedDevice(MacAddress Mac, string? DisplayName);
