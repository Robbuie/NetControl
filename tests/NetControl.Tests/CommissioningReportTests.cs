using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Commissioning;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Reporting;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The commissioning report, and the reading of the record underneath it that decides what a device's
/// state is. The report reads the project file and nothing else, so every test here builds a record
/// and checks what comes out - including one written by a real Set static against the simulator.
/// </summary>
public sealed class CommissioningReportTests : IDisposable
{
    private static readonly MacAddress Drive = MacAddress.Parse("00:1D:9C:C7:B0:70");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Line 3 panel");

    public void Dispose() => _project.Dispose();

    [Fact]
    public void AVerifiedReadbackInTheRecordIsVerified()
    {
        long id = Plan("192.168.1.51");
        Finished(id, "Verified", readback: "ip=192.168.1.51 mask=255.255.255.0 gw=0.0.0.0");

        DeviceCommissioning last = CommissioningRecord.LastOutcomes(_project.Events.All())[id];

        Assert.Equal(CommissioningOutcome.Verified, last.Outcome);
    }

    /// <summary>The last operation decides: a verified hand-back after a verified Set static is not commissioned.</summary>
    [Fact]
    public void AHandBackAfterwardsIsNotVerified()
    {
        long id = Plan("192.168.1.51");
        Finished(id, "Verified");
        Finished(id, "Verified", operation: "enableBootp");

        Assert.Equal(CommissioningOutcome.HandedBack, CommissioningRecord.LastOutcomes(_project.Events.All())[id].Outcome);
    }

    [Fact]
    public void AFailedRetryAfterASuccessIsNotVerified()
    {
        long id = Plan("192.168.1.51");
        Finished(id, "Verified");
        Finished(id, "Mismatch");

        Assert.Equal(CommissioningOutcome.NotVerified, CommissioningRecord.LastOutcomes(_project.Events.All())[id].Outcome);
    }

    /// <summary>Progress steps, diagnostics reads and hand-edited junk are not outcomes, and are not read as any.</summary>
    [Fact]
    public void IgnoresRowsThatAreNotAFinishedOperation()
    {
        long id = Plan("192.168.1.51");
        _project.Events.Append(EventSeverity.Info, EventCategory.Cip, "Reading Configuration Capability.", deviceId: id);
        _project.Events.Append(EventRecordWithDetail(id, "{not json"));
        _project.Events.Append(EventSeverity.Info, EventCategory.Cip, "Diagnostics read: fine.", target: "192.168.1.51",
            detail: new EventDetail().Add("operation", "readDiagnostics"));

        Assert.Empty(CommissioningRecord.LastOutcomes(_project.Events.All()));
    }

    [Fact]
    public void TheReportCarriesEveryDeviceItsStateAndTheWholeLog()
    {
        long verified = Plan("192.168.1.51", role: "Conveyor 3 drive");
        Plan("192.168.1.52", mac: "00:1D:9C:C7:B0:71", role: "Remote I/O");
        Finished(verified, "Verified", readback: "ip=192.168.1.51 mask=255.255.255.0 gw=0.0.0.0");
        _project.Events.Append(EventSeverity.Warn, EventCategory.Scan, "192.168.1.60 moved.");

        string html = CommissioningReport.BuildHtml(_project, Events.At, "NetControl 0.8.0");

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("Line 3 panel", html, StringComparison.Ordinal);
        Assert.Contains("Conveyor 3 drive", html, StringComparison.Ordinal);
        Assert.Contains("Remote I/O", html, StringComparison.Ordinal);
        Assert.Contains(">Verified<", html, StringComparison.Ordinal);
        Assert.Contains(">Planned<", html, StringComparison.Ordinal);
        Assert.Contains("192.168.1.60 moved.", html, StringComparison.Ordinal);
        Assert.Contains("NetControl 0.8.0", html, StringComparison.Ordinal);
        Assert.Contains("2026-08-07 09:30:00 UTC", html, StringComparison.Ordinal);
    }

    /// <summary>A project file is a database somebody may have edited. Markup in it comes out as text.</summary>
    [Fact]
    public void EncodesEverythingThatCameOutOfTheFile()
    {
        _project.Devices.Upsert(new DeviceRecord
        {
            Mac = Drive,
            PlannedIp = IPAddress.Parse("192.168.1.51"),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
            Notes = "<script>alert(1)</script> & \"quoted\"",
        });

        string html = CommissioningReport.BuildHtml(_project, Events.At);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>An address edited after the device was verified is not the address that was verified.</summary>
    [Fact]
    public void ADeviceReaddressedAfterVerificationIsNotReportedVerified()
    {
        long id = Plan("192.168.1.51");
        Finished(id, "Verified", readback: "ip=192.168.1.51 mask=255.255.255.0 gw=0.0.0.0");

        DeviceRecord moved = _project.Devices.Get(id)! with { PlannedIp = IPAddress.Parse("192.168.1.99") };
        _project.Devices.Update(moved);

        string html = CommissioningReport.BuildHtml(_project, Events.At);

        Assert.DoesNotContain(">Verified<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyProjectStillMakesAReport()
    {
        string html = CommissioningReport.BuildHtml(_project, Events.At);

        Assert.Contains("The plan is empty.", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verified survives closing the project. Set static against the simulator, then a fresh grid on
    /// the same record: the row comes back Verified, because the readback is in the record.
    /// </summary>
    [Fact]
    public async Task AReopenedPlanRemembersWhatWasVerified()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        var commissioner = new StaticIpCommissioner
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            VerifyAttempts = 5,
            VerifyDelay = TimeSpan.FromMilliseconds(20),
        };

        var grid = new DeviceGridViewModel(OuiDatabase.Empty, commissioner) { CommissionPort = adapter.Port };
        grid.Load(_project);
        grid.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = grid.Rows[0];
        row.MacText = Drive.ToString();
        row.IpText = IPAddress.Loopback.ToString();
        row.MaskText = "255.255.255.0";
        grid.SelectedRow = row;

        await grid.SetStaticCommand.ExecuteAsync(null);
        Assert.Equal(DeviceState.Verified, row.State);

        var reopened = new DeviceGridViewModel(OuiDatabase.Empty);
        reopened.Load(_project);

        Assert.Equal(DeviceState.Verified, Assert.Single(reopened.Rows).State);
    }

    private long Plan(string ip, string mac = "00:1D:9C:C7:B0:70", string? role = null) =>
        _project.Devices.Upsert(new DeviceRecord
        {
            Mac = MacAddress.Parse(mac),
            PlannedIp = IPAddress.Parse(ip),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
            Role = role,
        });

    /// <summary>The row the grid writes when an operation finishes - the shape the record is read for.</summary>
    private void Finished(long deviceId, string outcome, string? operation = null, string? readback = null) =>
        _project.Events.Append(
            outcome == "Verified" ? EventSeverity.Info : EventSeverity.Error,
            EventCategory.Cip,
            $"Finished: {outcome}.",
            deviceId: deviceId,
            detail: new EventDetail()
                .Add("operation", operation)
                .Add("outcome", outcome)
                .Add("readback", readback));

    private EventRecord EventRecordWithDetail(long deviceId, string detail) => new()
    {
        Utc = Events.At,
        Category = EventCategory.Cip,
        DeviceId = deviceId,
        Message = "Hand-edited.",
        Detail = detail,
    };
}
