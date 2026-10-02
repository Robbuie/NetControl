using System.Net;
using System.Text;
using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.Core.Dhcp;
using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The one sentence the TFTP tab exists to say: which step of an image backup stopped.
///
/// <para>Every case here is a situation somebody will be standing in front of a robot in, and the
/// assertions are mostly about two things: the sentence names the <em>earliest</em> step that is not
/// known to have worked, and nothing ever turns green without having been seen. A step that was
/// never observed reading as fine is the failure this whole panel exists to prevent.</para>
/// </summary>
public sealed class BackupVerdictTests
{
    private const string Controller = "00:E0:E4:12:34:56 (FANUC CORPORATION)";

    [Fact]
    public void With_nothing_running_every_step_is_grey_and_the_sentence_says_to_start_watching()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence());

        Assert.All(result.Steps, step => Assert.Equal(ReadinessState.Unknown, step.State));
        Assert.Equal(ReadinessState.Unknown, result.Overall);
        Assert.StartsWith(BackupVerdict.AddressRequestStep, result.Headline, StringComparison.Ordinal);
        Assert.Contains("listener is stopped", result.Headline, StringComparison.Ordinal);
        Assert.Contains("Press Watch", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void Listening_with_no_request_yet_points_at_the_cable_not_at_the_server()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = ServerRunState.Listening,
        });

        Assert.Equal(ReadinessState.Unknown, Step(result, BackupVerdict.AddressRequestStep).State);
        Assert.Contains("No address request has arrived", result.Headline, StringComparison.Ordinal);
        Assert.Contains("patch lead", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_seen_in_watch_mode_is_step_one_done_and_step_two_unknown()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = ServerRunState.Listening,
            AddressRequests = 1,
            Requester = Controller,
            RequesterAttempts = 1,
        });

        Assert.Equal(ReadinessState.Ready, Step(result, BackupVerdict.AddressRequestStep).State);
        Assert.Contains("once", Step(result, BackupVerdict.AddressRequestStep).Summary, StringComparison.Ordinal);

        // Watch mode cannot see another server's answer, so it must not guess either way.
        Assert.Equal(ReadinessState.Unknown, Step(result, BackupVerdict.AddressStep).State);
        Assert.StartsWith(BackupVerdict.AddressStep, result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_controller_that_keeps_asking_was_not_answered()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = ServerRunState.Listening,
            AddressRequests = BackupVerdict.UnansweredAfterAttempts,
            Requester = Controller,
            RequesterAttempts = BackupVerdict.UnansweredAfterAttempts,
        });

        ReadinessCheck address = Step(result, BackupVerdict.AddressStep);
        Assert.Equal(ReadinessState.Warning, address.State);
        Assert.Contains("keeps asking", address.Summary, StringComparison.Ordinal);
        Assert.StartsWith(BackupVerdict.AddressStep, result.Headline, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Warning, result.Overall);
    }

    [Fact]
    public void Serve_mode_that_did_not_answer_says_the_device_is_not_planned()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = ServerRunState.Listening,
            DhcpMode = DhcpServerMode.Serve,
            AddressRequests = 1,
            Requester = Controller,
            RequesterAttempts = 1,
        });

        Assert.Equal(ReadinessState.Warning, Step(result, BackupVerdict.AddressStep).State);
        Assert.Contains("did not answer", result.Headline, StringComparison.Ordinal);
        Assert.Contains("plan", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void Addressed_but_no_file_request_points_at_the_server_address_on_the_controller()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = ServerRunState.Listening,
            DhcpMode = DhcpServerMode.Serve,
            AddressRequests = 1,
            Requester = Controller,
            RequesterAttempts = 1,
            ServedAddress = IPAddress.Parse("192.168.1.51"),
            WatchState = ServerRunState.Listening,
        });

        Assert.Equal(ReadinessState.Ready, Step(result, BackupVerdict.AddressStep).State);
        Assert.Equal(ReadinessState.Unknown, Step(result, BackupVerdict.FileRequestStep).State);
        Assert.StartsWith(BackupVerdict.FileRequestStep, result.Headline, StringComparison.Ordinal);
        Assert.Contains("TFTP server address set on the controller", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_firewall_without_a_rule_is_named_when_no_file_request_arrives()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = ServerRunState.Listening,
            AddressRequests = 1,
            Requester = Controller,
            RequesterAttempts = 1,
            ServedAddress = IPAddress.Parse("192.168.1.51"),
            WatchState = ServerRunState.Listening,
            Firewall = new ReadinessCheck("Firewall", ReadinessState.Warning, "No inbound rule allows UDP/69."),
        });

        Assert.Contains("No inbound rule allows UDP/69.", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_request_from_the_addressed_controller_means_the_robot_side_works()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = ServerRunState.Listening,
            DhcpMode = DhcpServerMode.Serve,
            AddressRequests = 1,
            Requester = Controller,
            RequesterAttempts = 1,
            ServedAddress = IPAddress.Parse("192.168.1.51"),
            WatchState = ServerRunState.Listening,
            FileRequest = TftpRequests.Write("ROBOT1\\FROM00.IMG", source: "192.168.1.51"),
            FileRequests = 1,
        });

        ReadinessCheck file = Step(result, BackupVerdict.FileRequestStep);
        Assert.Equal(ReadinessState.Ready, file.State);
        Assert.Contains("ROBOT1\\FROM00.IMG", file.Summary, StringComparison.Ordinal);
        Assert.Contains("same controller", file.Summary, StringComparison.Ordinal);

        Assert.StartsWith("The robot side works", result.Headline, StringComparison.Ordinal);
        Assert.Contains("overwritten", result.Remediation, StringComparison.Ordinal);

        // The watch never receives the image, so the backup as a whole is never reported as fine.
        Assert.Equal(ReadinessState.Unknown, Step(result, BackupVerdict.TransferStep).State);
        Assert.Equal(ReadinessState.Unknown, result.Overall);
    }

    [Fact]
    public void A_file_request_from_some_other_address_is_not_claimed_as_the_same_controller()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            ServedAddress = IPAddress.Parse("192.168.1.51"),
            AddressRequests = 1,
            RequesterAttempts = 1,
            WatchState = ServerRunState.Listening,
            FileRequest = TftpRequests.Write("FROM00.IMG", source: "192.168.1.77"),
        });

        Assert.DoesNotContain("same controller", Step(result, BackupVerdict.FileRequestStep).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_netascii_request_is_a_warning_because_the_image_would_arrive_corrupted()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            AddressRequests = 1,
            RequesterAttempts = 1,
            ServedAddress = IPAddress.Parse("192.168.1.51"),
            WatchState = ServerRunState.Listening,
            FileRequest = TftpRequests.Write(
                "FROM00.IMG",
                source: "192.168.1.51",
                mode: TftpTransferMode.NetAscii,
                rawMode: "netascii"),
        });

        Assert.Equal(ReadinessState.Warning, Step(result, BackupVerdict.FileRequestStep).State);
        Assert.StartsWith(BackupVerdict.FileRequestStep, result.Headline, StringComparison.Ordinal);
        Assert.Contains("netascii", result.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watch_that_died_is_blocked_and_says_why()
    {
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            AddressRequests = 1,
            RequesterAttempts = 1,
            ServedAddress = IPAddress.Parse("192.168.1.51"),
            WatchState = ServerRunState.Faulted,
            WatchStopReason = "The listening socket failed: NetworkDown.",
        });

        ReadinessCheck file = Step(result, BackupVerdict.FileRequestStep);
        Assert.Equal(ReadinessState.Blocked, file.State);
        Assert.Contains("NetworkDown", file.Summary, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Blocked, result.Overall);
    }

    [Fact]
    public void The_earliest_problem_wins_over_a_later_one()
    {
        // Step 2 is a warning and step 3 is blocked. The sentence is about step 2: a controller that
        // never took an address was never going to send a file, so the later failure is a consequence.
        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = ServerRunState.Listening,
            AddressRequests = 5,
            Requester = Controller,
            RequesterAttempts = 5,
            WatchState = ServerRunState.Faulted,
        });

        Assert.StartsWith(BackupVerdict.AddressStep, result.Headline, StringComparison.Ordinal);
        Assert.Equal(ReadinessState.Blocked, result.Overall);
    }

    private static ReadinessCheck Step(BackupVerdictResult result, string name) =>
        Assert.Single(result.Steps, s => s.Name == name);
}

/// <summary>File requests as the watch reports them, for tests that need one without a socket.</summary>
internal static class TftpRequests
{
    public static TftpRequestEventArgs Write(
        string fileName,
        string source = "192.168.1.51",
        int sourcePort = 50000,
        TftpTransferMode mode = TftpTransferMode.Octet,
        string rawMode = "octet",
        bool isRetransmit = false,
        TftpWatchAction action = TftpWatchAction.Refused) =>
        new(
            Events.At,
            new TftpRequestMessage(
                TftpOpcode.WriteRequest,
                fileName,
                Encoding.Latin1.GetBytes(fileName),
                mode,
                rawMode,
                TftpOptions.None),
            new IPEndPoint(IPAddress.Parse(source), sourcePort),
            12,
            Nics.Healthy(),
            IPAddress.Parse("192.168.1.10"),
            isRetransmit,
            action,
            "recorded, and refused from UDP/69 so the controller stops rather than retransmitting");
}
