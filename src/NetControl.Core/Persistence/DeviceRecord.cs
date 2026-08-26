using System.Net;
using NetControl.Core.Dhcp;

namespace NetControl.Core.Persistence;

/// <summary>
/// One row of the plan: a MAC, the address it is supposed to get, and everything a human needs to
/// find the thing in a panel.
///
/// The stored form is deliberately looser than <see cref="DeviceAssignment"/>. A plan gets typed
/// in and imported from CSV before it is complete - a MAC written on a label at 7am with the
/// address still to be decided is a normal row - so the address fields are nullable here and the
/// validation lives in <see cref="TryToAssignment"/>. Anything that cannot become a valid
/// assignment simply never reaches the server, which is the behaviour that matters: an incomplete
/// row must not be servable.
/// </summary>
public sealed record DeviceRecord
{
    /// <summary>Row id. 0 for a record that has not been written yet.</summary>
    public long Id { get; init; }

    /// <summary>The primary key that matters. Unique in the file.</summary>
    public required MacAddress Mac { get; init; }

    public IPAddress? PlannedIp { get; init; }

    public IPAddress? PlannedMask { get; init; }

    public IPAddress? PlannedGateway { get; init; }

    public string? HostName { get; init; }

    /// <summary>Panel, tag or drawing reference - how somebody finds this device physically.</summary>
    public string? PanelRef { get; init; }

    /// <summary>Free text: "PowerFlex 525 conveyor 3".</summary>
    public string? Role { get; init; }

    /// <summary>Resolved from the OUI at import time, so the file still reads correctly if the OUI table moves on.</summary>
    public string? Vendor { get; init; }

    /// <summary>Stored in the <c>QuirkFlags</c> column.</summary>
    public DeviceQuirks Quirks { get; init; }

    public string? Notes { get; init; }

    /// <summary>Has enough of an address to be worth trying to serve. Not the same as valid.</summary>
    public bool IsPlanned => PlannedIp is not null && PlannedMask is not null;

    /// <summary>
    /// What to call this device on screen, or null when nobody has labelled it yet.
    ///
    /// <para>The precedence is <see cref="Role"/>, then <see cref="HostName"/>, then
    /// <see cref="PanelRef"/>. <c>Role</c> first because it is what a person says out loud -
    /// "the conveyor 3 drive" - and a log read against a printed plan is read by a person. The
    /// other two are fallbacks so that a row carrying any label at all gets one, rather than
    /// showing a MAC because the label went in a column this happened not to check.</para>
    ///
    /// <para>It lives here, and not in a view model, for the same reason the amber serve wording
    /// does: the grid, the live log and any report that follows must not be able to disagree about
    /// what a device is called. One rule, one place.</para>
    /// </summary>
    public string? DisplayName => FirstNonBlank(Role, HostName, PanelRef);

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a stored row into something the DHCP engine will serve, or explains why it will
    /// not. The explanation is the point: "row 14: 255.255.0.255 is not a valid subnet mask" is
    /// what turns a silent no-show on site into a five-second fix at the desk.
    /// </summary>
    public bool TryToAssignment(out DeviceAssignment? assignment, out string? problem)
    {
        assignment = null;

        if (PlannedIp is null)
        {
            problem = $"{Mac} has no planned address";
            return false;
        }

        if (PlannedMask is null)
        {
            problem = $"{Mac} has a planned address ({PlannedIp}) but no subnet mask";
            return false;
        }

        try
        {
            assignment = new DeviceAssignment(Mac, PlannedIp, PlannedMask, PlannedGateway, HostName);
            problem = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            // DeviceAssignment does the real validation - contiguous mask, IPv4, usable MAC. A
            // row that fails it came from a CSV or a typed cell, so this is a data problem to
            // report, not a bug to crash on.
            problem = $"{Mac}: {ex.Message}";
            return false;
        }
    }
}
