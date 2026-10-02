using System.Globalization;
using NetControl.App.Serving;
using NetControl.Core.Dhcp;
using NetControl.Core.Tftp;

namespace NetControl.App.Diagnostics;

/// <summary>
/// PLAN-TFTP.md part E5: which step of an image backup failed, in one sentence.
///
/// <para>A FANUC image backup over Ethernet is four steps, and today a failure in any of them looks
/// the same at the pendant: the controller asks for an address, gets one, asks the TFTP server to
/// take the file, and sends it. This walks the steps in that order and names the earliest one not
/// known to have worked. That sentence is the deliverable of the whole TFTP plan.</para>
///
/// <para><b>Grey is not green, here above all.</b> Every step starts <see cref="ReadinessState.Unknown"/>
/// and only becomes Ready on evidence - a request actually seen, an address actually sent. "We never
/// saw a file request" must never read as "the file request is fine", which is the same rule
/// <see cref="ReadinessState"/> already holds for the interface bar.</para>
///
/// <para>Pure: everything comes in through <see cref="BackupEvidence"/>, so every sentence it can
/// produce is reachable from a test.</para>
/// </summary>
public static class BackupVerdict
{
    public const string AddressRequestStep = "Asked for an address";
    public const string AddressStep = "Got an address";
    public const string FileRequestStep = "Asked for the file";
    public const string TransferStep = "Sent the file";

    /// <summary>
    /// Attempts after which a controller that is still asking is treated as not having been
    /// answered. A BOOTP client retries on a backoff of a few seconds; three attempts is past any
    /// ordinary race between the request and a reply that was on its way.
    /// </summary>
    public const int UnansweredAfterAttempts = 3;

    public static BackupVerdictResult Evaluate(BackupEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        IReadOnlyList<ReadinessCheck> steps =
        [
            AddressRequest(evidence),
            Address(evidence),
            FileRequest(evidence),
            Transfer(evidence),
        ];

        ReadinessCheck? lead = steps.FirstOrDefault(s => s.State is ReadinessState.Blocked or ReadinessState.Warning)
            ?? steps.FirstOrDefault(s => s.State is ReadinessState.Unknown);

        ReadinessState overall = ReadinessCheck.Worst(steps);

        if (lead is null)
        {
            // Only Accept mode gets here: the file was received, so every step has been seen.
            return new BackupVerdictResult(
                steps,
                "The whole backup worked into NetControl, so the robot and the network are fine.",
                "If the same backup fails against the real TFTP server, the cause is that server's "
                    + "configuration: its root folder, the file names it accepts, and its overwrite setting.",
                overall);
        }

        if (lead.Name == TransferStep && !evidence.AcceptMode && evidence.FileRequest is { } request)
        {
            return new BackupVerdictResult(
                steps,
                $"The robot side works: it got an address and asked for '{request.Request.FileName}'. "
                    + "What is left is the TFTP server accepting it.",
                lead.Remediation,
                overall);
        }

        return new BackupVerdictResult(steps, $"{lead.Name}: {lead.Summary}", lead.Remediation, overall);
    }

    private static ReadinessCheck AddressRequest(BackupEvidence e)
    {
        if (e.AddressRequests > 0)
        {
            string who = e.Requester ?? "A device";
            return new ReadinessCheck(
                AddressRequestStep,
                ReadinessState.Ready,
                $"{who} asked for an address ({Times(e.RequesterAttempts)}).");
        }

        return e.DhcpState switch
        {
            ServerRunState.Listening or ServerRunState.Starting => ReadinessCheck.NotChecked(
                AddressRequestStep,
                "Listening on UDP/67. No address request has arrived yet.",
                "Start the backup on the pendant. If nothing ever arrives, the request is not reaching "
                    + "this PC: check the patch lead, the switch port and the VLAN, and that this PC is on "
                    + "the robot network."),
            ServerRunState.Faulted => new ReadinessCheck(
                AddressRequestStep,
                ReadinessState.Blocked,
                "The BOOTP/DHCP listener stopped on a fault, so the address request cannot be seen.",
                "Start it again with Watch at the top of the window."),
            _ => ReadinessCheck.NotChecked(
                AddressRequestStep,
                "Not being watched - the BOOTP/DHCP listener is stopped.",
                "Press Watch at the top of the window (or Serve, if NetControl is to hand out the "
                    + "address) so the controller's request can be seen."),
        };
    }

    private static ReadinessCheck Address(BackupEvidence e)
    {
        string who = e.Requester ?? "The device";

        if (e.ServedAddress is { } served)
        {
            return new ReadinessCheck(
                AddressStep,
                ReadinessState.Ready,
                $"NetControl gave {who} the address {served}.");
        }

        if (e.AddressRequests == 0)
        {
            return ReadinessCheck.NotChecked(AddressStep, "Waiting for the address request.");
        }

        if (e.DhcpMode == DhcpServerMode.Serve)
        {
            return new ReadinessCheck(
                AddressStep,
                ReadinessState.Warning,
                $"{who} asked, and NetControl did not answer it.",
                "Serve mode only answers devices in the plan that have an address. Double-click the "
                    + "controller's row in Live requests to plan it and type the address it should have; it "
                    + "is served on its next retry.");
        }

        if (e.RequesterAttempts >= UnansweredAfterAttempts)
        {
            return new ReadinessCheck(
                AddressStep,
                ReadinessState.Warning,
                $"{who} has asked {Times(e.RequesterAttempts)}. A controller that keeps asking either got "
                    + "no answer or ignored the one it got.",
                "If the plant's own BOOTP server is meant to answer, check it is running and holds this "
                    + "MAC. If it is answering and the controller still keeps asking, the reply is being "
                    + "ignored - a reply that is too short or leaves by the wrong adapter are the usual "
                    + "causes. Planning the controller here and using Serve rules both out.");
        }

        return ReadinessCheck.NotChecked(
            AddressStep,
            $"{who} asked. Watch mode cannot see whether another server answered.",
            "A request that stops repeating usually got its answer. If it keeps repeating, nobody is "
                + "answering it.");
    }

    private static ReadinessCheck FileRequest(BackupEvidence e)
    {
        if (e.FileRequest is { } r)
        {
            string verb = r.Request.IsWrite ? "write" : "read";
            string source = r.Source.Address.ToString();
            string same = e.ServedAddress is { } served && served.Equals(r.Source.Address)
                ? " - the same controller NetControl just addressed"
                : string.Empty;
            string what = $"{source} asked to {verb} '{r.Request.FileName}' in {r.Request.RawMode} mode{same}";

            if (e.AcceptMode && r.Action is TftpWatchAction.Refused or TftpWatchAction.SendFailed)
            {
                return new ReadinessCheck(
                    FileRequestStep,
                    ReadinessState.Warning,
                    $"{what}, and it was {r.Reason}.",
                    "A real TFTP server refuses this for the same reason. Fix the name or the folder, then "
                        + "run the backup again.");
            }

            IReadOnlyList<string> concerns = r.Request.Concerns();
            if (concerns.Count > 0)
            {
                return new ReadinessCheck(
                    FileRequestStep,
                    ReadinessState.Warning,
                    $"{what}, but: {concerns[0]}",
                    "Correct the backup settings on the controller, then run it again.");
            }

            return new ReadinessCheck(
                FileRequestStep,
                ReadinessState.Ready,
                $"{what}. The network path from the robot to this PC works.");
        }

        switch (e.WatchState)
        {
            case ServerRunState.Listening or ServerRunState.Starting:
            {
                string remediation = e.AddressRequests > 0 || e.ServedAddress is not null
                    ? "The controller has asked for its address but not for the file. Check the TFTP "
                        + "server address set on the controller, and that nothing between the robot and "
                        + "this PC drops UDP/69."
                    : "Start the backup on the pendant.";

                if (e.Firewall is { State: ReadinessState.Warning or ReadinessState.Blocked } firewall)
                {
                    remediation += " This PC's firewall: " + firewall.Summary;
                }

                return ReadinessCheck.NotChecked(
                    FileRequestStep,
                    "Watching UDP/69. No file request has arrived yet.",
                    remediation);
            }

            case ServerRunState.Faulted:
                return new ReadinessCheck(
                    FileRequestStep,
                    ReadinessState.Blocked,
                    e.WatchStopReason ?? "The TFTP watch stopped on a fault.",
                    "Press Watch TFTP to start it again.");

            default:
            {
                string port = e.ServerPort is { } check ? " " + check.Summary : string.Empty;
                return ReadinessCheck.NotChecked(
                    FileRequestStep,
                    "Not being watched - the TFTP watch is stopped.",
                    "Stop the real TFTP server, then press Watch TFTP to see whether the controller asks "
                        + "for its file." + port);
            }
        }
    }

    private static ReadinessCheck Transfer(BackupEvidence e)
    {
        if (e.Transfer is { IsWrite: true } transfer)
        {
            return transfer.Outcome.Succeeded
                ? new ReadinessCheck(
                    TransferStep,
                    ReadinessState.Ready,
                    $"NetControl received '{transfer.Request.FileName}': {transfer.Outcome.Stats.Describe()}.")
                : new ReadinessCheck(
                    TransferStep,
                    ReadinessState.Blocked,
                    transfer.Outcome.Failure ?? "The transfer did not finish.",
                    transfer.Outcome.Remediation);
        }

        if (e.FileRequest is not { } r)
        {
            return ReadinessCheck.NotChecked(TransferStep, "Not observed yet.");
        }

        if (e.AcceptMode && r.Action == TftpWatchAction.Accepted)
        {
            return ReadinessCheck.NotChecked(TransferStep, $"Receiving '{r.Request.FileName}' now...");
        }

        if (e.AcceptMode)
        {
            return ReadinessCheck.NotChecked(TransferStep, "Not started - the request was refused.");
        }

        return ReadinessCheck.NotChecked(
            TransferStep,
            "Not observed. The watch records the request and refuses it on purpose - it never receives "
                + "the image.",
            "Start the real TFTP server again and run the backup. If it still fails, the server is "
                + $"refusing '{r.Request.FileName}': check that its root folder exists and is writable, "
                + "that it accepts that file name (including any folder in it), and whether it allows "
                + "an existing backup to be overwritten.");
    }

    private static string Times(int attempts) => attempts switch
    {
        <= 1 => "once",
        2 => "twice",
        _ => attempts.ToString(CultureInfo.InvariantCulture) + " times",
    };
}
