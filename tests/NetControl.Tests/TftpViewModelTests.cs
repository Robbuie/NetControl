using System.Net;
using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The TFTP tab's view model, driven without a socket: the watch is never started here, and file
/// requests arrive through <see cref="TftpViewModel.NoteFileRequest"/> exactly as the controller
/// would hand them over.
/// </summary>
public sealed class TftpViewModelTests : IDisposable
{
    private static readonly MacAddress Robot = MacAddress.Parse("00:E0:E4:12:34:56");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory();
    private readonly PortStubPreflight _preflight = new();
    private AdapterOption? _adapter;
    private readonly TftpViewModel _viewModel;

    public TftpViewModelTests()
    {
        _viewModel = new TftpViewModel(
            new ImmediateDispatcher(),
            _preflight,
            new TftpWatchController(new FakeNicInventory()),
            () => _project,
            () => _adapter,
            OuiDatabase.Empty,
            new TestTimeProvider(Events.At));
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _project.Dispose();
    }

    [Fact]
    public void The_watch_cannot_start_until_armed_with_an_adapter_selected()
    {
        Assert.False(_viewModel.StartWatchCommand.CanExecute(null));

        _viewModel.IsWatchArmed = true;
        Assert.False(_viewModel.StartWatchCommand.CanExecute(null));

        // A refusal is a transmission, and it only ever leaves by the adapter somebody chose.
        _adapter = new AdapterOption(Nics.Healthy());
        _viewModel.NoteAdapterChanged();
        Assert.True(_viewModel.StartWatchCommand.CanExecute(null));
    }

    [Fact]
    public void A_file_request_lands_in_the_list_with_its_name_exactly_as_sent()
    {
        _viewModel.NoteFileRequest(TftpRequests.Write(" Robot1\\FROM00.IMG "));

        TftpRequestRowViewModel row = Assert.Single(_viewModel.Requests);
        Assert.Equal(" Robot1\\FROM00.IMG ", row.FileName);
        Assert.Equal("WRQ (backup)", row.OperationText);
        Assert.Equal(LogEntryKind.Request, row.Kind);
    }

    [Fact]
    public void A_retransmit_counts_on_its_own_row_instead_of_adding_one()
    {
        _viewModel.NoteFileRequest(TftpRequests.Write("FROM00.IMG"));
        _viewModel.NoteFileRequest(TftpRequests.Write("FROM00.IMG", isRetransmit: true));
        _viewModel.NoteFileRequest(TftpRequests.Write("FROM00.IMG", isRetransmit: true));

        TftpRequestRowViewModel row = Assert.Single(_viewModel.Requests);
        Assert.Equal(3, row.RepeatCount);
        Assert.Equal("x3", row.RepeatText);
    }

    [Fact]
    public void A_fresh_attempt_from_a_new_port_is_a_new_row()
    {
        // Address and port together are the client's transfer identifier. A controller that gave up
        // and started again comes back from a different port, and that is a second attempt.
        _viewModel.NoteFileRequest(TftpRequests.Write("FROM00.IMG", sourcePort: 50000));
        _viewModel.NoteFileRequest(TftpRequests.Write("FROM00.IMG", sourcePort: 50001));

        Assert.Equal(2, _viewModel.Requests.Count);
    }

    [Fact]
    public void A_netascii_request_is_flagged_on_its_row()
    {
        _viewModel.NoteFileRequest(TftpRequests.Write(
            "FROM00.IMG",
            mode: NetControl.Core.Tftp.TftpTransferMode.NetAscii,
            rawMode: "netascii"));

        TftpRequestRowViewModel row = Assert.Single(_viewModel.Requests);
        Assert.True(row.HasConcern);
        Assert.Equal(LogEntryKind.Warning, row.Kind);
    }

    [Fact]
    public void Both_halves_of_a_backup_reach_the_verdict()
    {
        _viewModel.NoteDhcpState(ServerRunState.Listening, DhcpServerMode.Serve);
        _viewModel.NoteDhcpRequest(Events.Request(Robot, AssignmentDecision.Ignore("planned")));
        _viewModel.NoteDhcpReply(Events.Reply(Events.Assignment(Robot, "192.168.1.51")));
        _viewModel.NoteFileRequest(TftpRequests.Write("FROM00.IMG", source: "192.168.1.51"));

        Assert.Equal(ReadinessState.Ready, _viewModel.Steps[0].State);
        Assert.Equal(ReadinessState.Ready, _viewModel.Steps[1].State);
        Assert.Contains("192.168.1.51", _viewModel.Steps[1].Summary, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Ready, _viewModel.Steps[2].State);
        Assert.Contains("same controller", _viewModel.Steps[2].Summary, StringComparison.Ordinal);
        Assert.StartsWith("The robot side works", _viewModel.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reply_without_an_address_is_not_step_two()
    {
        _viewModel.NoteDhcpState(ServerRunState.Listening, DhcpServerMode.Serve);
        _viewModel.NoteDhcpRequest(Events.Request(Robot, AssignmentDecision.Ignore("not planned")));

        DhcpReplyEventArgs reply = Events.Reply(Events.Assignment(Robot));
        var refusal = new DhcpReplyEventArgs(
            reply.Timestamp,
            reply.Request,
            reply.Reply,
            assignment: null,
            reply.Nic,
            reply.SendMode,
            reply.Destination,
            reply.ByteCount);
        _viewModel.NoteDhcpReply(refusal);

        Assert.NotEqual(ReadinessState.Ready, _viewModel.Steps[1].State);
    }

    [Fact]
    public void Clear_forgets_both_halves()
    {
        _viewModel.NoteDhcpRequest(Events.Request(Robot, AssignmentDecision.Ignore("watch")));
        _viewModel.NoteFileRequest(TftpRequests.Write("FROM00.IMG"));

        _viewModel.ClearCommand.Execute(null);

        Assert.Empty(_viewModel.Requests);
        Assert.All(_viewModel.Steps, step => Assert.NotEqual(ReadinessState.Ready, step.State));
    }

    [Fact]
    public async Task On_the_backup_server_nothing_holding_the_port_is_the_fault()
    {
        _viewModel.IsBackupServer = true;
        await _viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(ReadinessState.Blocked, _viewModel.PortCheck.State);
        Assert.Contains("no TFTP server is running", _viewModel.PortCheck.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_laptop_nothing_holding_the_port_means_the_watch_can_bind()
    {
        await _viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(ReadinessState.Ready, _viewModel.PortCheck.State);
    }

    [Fact]
    public async Task A_TFTP_server_holding_the_port_is_good_news_on_the_server_and_in_the_way_on_a_laptop()
    {
        _preflight.Owners = [new UdpPortOwner(IPAddress.Any, 69, 4312, "tftpd64")];

        await _viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(ReadinessState.Warning, _viewModel.PortCheck.State);
        Assert.Contains("tftpd64", _viewModel.PortCheck.Summary, StringComparison.Ordinal);

        // Same fact, other vantage. Changing the tick regrades without asking Windows again.
        _viewModel.IsBackupServer = true;
        Assert.Equal(ReadinessState.Ready, _viewModel.PortCheck.State);
        Assert.Contains("TFTP server is running", _viewModel.PortCheck.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_writable_folder_is_ready_and_a_missing_one_is_blocked()
    {
        string folder = Path.Combine(Path.GetTempPath(), "netcontrol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            _viewModel.RootFolder = folder;
            await _viewModel.CheckRootCommand.ExecuteAsync(null);
            Assert.NotEqual(ReadinessState.Blocked, _viewModel.RootCheck.State);
            Assert.NotEqual(ReadinessState.Unknown, _viewModel.RootCheck.State);

            _viewModel.RootFolder = Path.Combine(folder, "not-there");
            await _viewModel.CheckRootCommand.ExecuteAsync(null);
            Assert.Equal(ReadinessState.Blocked, _viewModel.RootCheck.State);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Editing_the_folder_drops_the_old_grade()
    {
        _viewModel.RootFolder = @"C:\TFTP-Root";

        Assert.Equal(ReadinessState.Unknown, _viewModel.RootCheck.State);
        Assert.Contains("Not checked yet", _viewModel.RootCheck.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_received_backup_completes_the_verdict_in_accept_mode()
    {
        _viewModel.IsAcceptMode = true;
        _viewModel.NoteDhcpState(ServerRunState.Listening, DhcpServerMode.Serve);
        _viewModel.NoteDhcpRequest(Events.Request(Robot, AssignmentDecision.Ignore("planned")));
        _viewModel.NoteDhcpReply(Events.Reply(Events.Assignment(Robot, "192.168.1.51")));
        _viewModel.NoteFileRequest(TftpRequests.Write(
            "FROM00.IMG",
            source: "192.168.1.51",
            action: NetControl.Core.Tftp.TftpWatchAction.Accepted));
        _viewModel.NoteTransfer(TftpRequests.Finished("FROM00.IMG", succeeded: true));

        Assert.Equal(ReadinessState.Ready, _viewModel.Overall);
        Assert.StartsWith("The whole backup worked", _viewModel.Verdict, StringComparison.Ordinal);
        Assert.Equal(LogEntryKind.Reply, _viewModel.Requests[0].Kind);
    }

    [Fact]
    public async Task The_probe_needs_an_address_and_a_tick_and_clears_the_tick_after_running()
    {
        NetControl.Core.Tftp.TftpProbeOptions? asked = null;
        using var probing = new TftpViewModel(
            new ImmediateDispatcher(),
            _preflight,
            new TftpWatchController(new FakeNicInventory()),
            () => _project,
            () => _adapter,
            OuiDatabase.Empty,
            new TestTimeProvider(Events.At),
            probe: (options, _, _) =>
            {
                asked = options;
                return Task.FromResult(new NetControl.Core.Tftp.TftpProbeResult
                {
                    Succeeded = true,
                    Summary = "The server took a 40.0 MB write and gave back an identical copy.",
                    FileName = options.FileName,
                    Findings = ["finding one"],
                });
            });

        Assert.False(probing.RunProbeCommand.CanExecute(null));

        probing.ProbeServer = "192.168.1";
        probing.ProbeConfirmed = true;
        Assert.False(probing.RunProbeCommand.CanExecute(null));

        probing.ProbeServer = "192.168.1.20";
        Assert.True(probing.RunProbeCommand.CanExecute(null));

        await probing.RunProbeCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), asked.Server);
        Assert.Equal(NetControl.Core.Tftp.TftpProbeOptions.DefaultSizeBytes, asked.SizeBytes);
        Assert.StartsWith("netcontrol-probe-", asked.FileName, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Ready, probing.ProbeState);
        Assert.Equal("finding one", Assert.Single(probing.ProbeFindings));

        // Every run leaves a file on a server, so the tick never carries over to the next one.
        Assert.False(probing.ProbeConfirmed);
        Assert.False(probing.RunProbeCommand.CanExecute(null));
    }

    [Fact]
    public void Settings_are_restored_and_saved_when_they_change()
    {
        TftpSettings? saved = null;
        using var remembering = new TftpViewModel(
            new ImmediateDispatcher(),
            _preflight,
            new TftpWatchController(new FakeNicInventory()),
            () => _project,
            () => _adapter,
            OuiDatabase.Empty,
            new TestTimeProvider(Events.At),
            settings: new TftpSettings { Folder = @"C:\TFTP-Root", IsBackupServer = true, ProbeServer = "10.0.0.5" },
            saveSettings: s => saved = s);

        Assert.Equal(@"C:\TFTP-Root", remembering.RootFolder);
        Assert.True(remembering.IsBackupServer);
        Assert.Equal("10.0.0.5", remembering.ProbeServer);
        Assert.Null(saved);

        remembering.AllowOverwrite = true;

        Assert.NotNull(saved);
        Assert.True(saved.AllowOverwrite);
        Assert.Equal(@"C:\TFTP-Root", saved.Folder);
    }

    /// <summary>A preflight for UDP/69 whose port owners the test sets.</summary>
    private sealed class PortStubPreflight : IPreflight
    {
        public IReadOnlyList<UdpPortOwner> Owners { get; set; } = [];

        public Task<PreflightResult> InspectAsync(int port, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PreflightResult(
                new PortConflictReport(
                    port,
                    Owners.Count == 0 ? PortConflictSeverity.None : PortConflictSeverity.Serious,
                    Owners,
                    Owners.Count == 0 ? $"UDP/{port} is free." : $"UDP/{port} is held.",
                    null),
                Preflights.Firewall(FirewallVerdict.Allowed, $"Inbound UDP/{port} is allowed.", port: port),
                Events.At));
    }
}
