using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NetControl.Core;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using NetControl.Core.Tftp;

namespace NetControl.Spike.TftpWatch;

/// <summary>
/// Watches UDP/69 and prints what a controller asks for, and - with <c>--send</c> - asks a TFTP
/// server for something itself and reports what comes back.
///
/// <para>This exists because <c>NetControl.App</c> has no TFTP surface yet, and BENCH.md Run 6b
/// needs one before a robot is down. It is a door into <see cref="TftpWatchServer"/>, not a second
/// implementation of anything: the codec, the watch and the readiness checks are all the shipping
/// ones, so whatever this prints is what the app will report once the tab exists.</para>
///
/// <para>The thing to bring back from a session with this is <b>the filename, verbatim</b>. Nothing
/// else on that laptop will tell you what a controller asked for when a server refused it.</para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// The name --send asks for unless told otherwise. Unmistakably a probe, because a server that
    /// accepts a write request generally creates the file before any data arrives.
    /// </summary>
    private const string DefaultProbeFile = "netcontrol-probe.tmp";

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Usage();
            return 0;
        }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Usage();
            return 2;
        }

        // Opened before anything is printed, so the file holds the whole run rather than the part
        // after the output somebody wanted to keep.
        using Transcript? transcript = OpenTranscript(options);

        if (options.ShowFirewallCommand)
        {
            ReportFirewallCommand(options.Port);
            return 0;
        }

        // --send is a client, not a watch: it needs no adapter inventory and no port of its own.
        if (options.SendTo is { } host)
        {
            return await SendAsync(host, options).ConfigureAwait(false);
        }

        // One inventory, shared: it subscribes to Windows change notifications and caches, so a
        // second one would be two subscriptions doing the same work.
        using var nics = new NicMonitor();

        ReportEnvironment(nics, options);

        if (options.ListOnly)
        {
            return 0;
        }

        using ProjectStore? project = OpenProject(options);

        if (options.ProjectPath is not null && project is null)
        {
            // Asked for a record and did not get one. Starting anyway produces a session that
            // afterwards looks exactly like one where the controller never asked for anything.
            return 1;
        }

        WarnIfNothingIsBeingKept(options);

        return await WatchAsync(nics, options, project).ConfigureAwait(false);
    }

    /// <summary>
    /// The transcript, or nothing and a sentence saying why. A failure here never stops the run:
    /// whoever is about to watch may already have stopped the plant's TFTP server to do it.
    /// </summary>
    private static Transcript? OpenTranscript(Options options)
    {
        if (options.LogPath is not { } path)
        {
            return null;
        }

        if (Transcript.TryOpen(path, out Transcript? transcript, out string? problem))
        {
            Console.WriteLine($"Transcript: {transcript!.Path}");
            Console.WriteLine();
            return transcript;
        }

        Console.Error.WriteLine($"Could not open the transcript '{path}': {problem}");
        Console.Error.WriteLine("Carrying on without one. Copy this window before you close it.");
        Console.Error.WriteLine();
        return null;
    }

    /// <summary>
    /// The project file, which is the actual record: append-only, and refused by the database
    /// itself if anything later tries to edit it. The transcript is what a person saw; this is what
    /// can be read back six months later by somebody who was not there.
    /// </summary>
    private static ProjectStore? OpenProject(Options options)
    {
        if (options.ProjectPath is not { } path)
        {
            return null;
        }

        try
        {
            ProjectStore store = ProjectStore.Open(path);

            Console.WriteLine($"Recording to {store.FilePath}");
            Console.WriteLine("  Every request, refusal and fault gets a row, and no row can be edited after.");
            Console.WriteLine();
            return store;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Could not open the project file '{path}': {ex.Message}");

            if (ex is NetControlException { Remediation: { } remediation })
            {
                Console.Error.WriteLine($"  {remediation}");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("Not starting. You asked for a record, and a watch that runs without one");
            Console.Error.WriteLine("looks afterwards exactly like a watch that saw nothing at all.");
            return null;
        }
    }

    /// <summary>
    /// Said once, out loud, because of what this is usually being run for. The output of BENCH.md
    /// Run 6b is three short strings printed once, in a plant, by somebody with a robot to put
    /// back - and a console scrollback is not a record of anything.
    /// </summary>
    private static void WarnIfNothingIsBeingKept(Options options)
    {
        if (options.LogPath is not null || options.ProjectPath is not null)
        {
            return;
        }

        Console.WriteLine("** Nothing is being written to disk. This window is the only copy of whatever");
        Console.WriteLine("   the controller asks for. --log <file> keeps a transcript, --project <file>");
        Console.WriteLine("   keeps the append-only record. Both is better.");
        Console.WriteLine();
    }

    /// <summary>
    /// The exact elevated command that lets inbound UDP/69 reach this executable, built by the same
    /// code the interface bar uses. Printed rather than run: binding a low port works as a standard
    /// user on Windows, and a tool that demands elevation is a tool that needs a ticket raised.
    /// </summary>
    private static void ReportFirewallCommand(int port)
    {
        Console.WriteLine($"Executable: {Environment.ProcessPath ?? "(unknown)"}");
        Console.WriteLine();
        Console.WriteLine("A firewall rule names an executable, so the rule has to name THIS one. Under");
        Console.WriteLine("'dotnet run' that path is inside bin\\Debug and moves with the configuration;");
        Console.WriteLine("tools/publish-tftp-spike.ps1 produces one that stays where you put it.");
        Console.WriteLine();
        Console.WriteLine("From an elevated prompt:");
        Console.WriteLine();
        Console.WriteLine($"  {FirewallCheck.BuildAddRuleCommand(port)}");
        Console.WriteLine();
        Console.WriteLine("And to take it away again afterwards:");
        Console.WriteLine();
        Console.WriteLine($"  netsh advfirewall firewall delete rule name=\"NetControl inbound UDP/{port}\"");
    }

    private static async Task<int> WatchAsync(INicInventory nics, Options options, ProjectStore? project)
    {
        var watch = new TftpWatchServer(
            nics,
            new TftpWatchOptions
            {
                ListenPort = options.Port,
                InterfaceIndexFilter = options.InterfaceIndex,
                SendRefusal = !options.Quiet,
                RefusalSource = options.RefuseFromEphemeral
                    ? TftpRefusalSource.TemporarySocket
                    : TftpRefusalSource.ListeningSocket,
            });

        // The shipping recorder, not one written for this spike. Same argument as the codec: what
        // gets recorded here has to be what the app will record, or the session proves nothing
        // about the app.
        using TftpEventRecorder? recorder = project is null
            ? null
            : new TftpEventRecorder(project, watch) { RecordRetransmits = options.RecordRetransmits };

        if (recorder is not null)
        {
            // A row that could not be written is worth a line on screen. The watch carries on
            // either way - a database problem must never cost the next request.
            recorder.RecordingFailed += (_, e) =>
                Console.Error.WriteLine($"          RECORD   : the {e.What} row was not written - {e.Message}");
        }

        watch.RequestReceived += (_, e) => PrintRequest(e);
        watch.Fault += (_, e) => PrintFault(e);
        watch.Listening += (_, _) =>
        {
            Console.WriteLine($"Listening on {watch.LocalEndPoint}.");
            Console.WriteLine("Nothing will be accepted; every request is recorded and refused.");
            Console.WriteLine("No file is stored anywhere - this watch has no destination folder, and");
            Console.WriteLine("--root only inspects one. Receiving a file is Accept mode, which is not built.");

            Console.WriteLine(options.RefuseFromEphemeral
                ? "Refusals go out of a fresh socket, so each carries a transfer identifier of its own."
                : "Refusals go out of this socket, so each arrives from the well-known port.");

            Console.WriteLine("Ctrl+C to stop - and start the real TFTP server again afterwards.");
            Console.WriteLine();
        };

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            await watch.RunAsync(cancellation.Token).ConfigureAwait(false);
            Stopped(recorder, project);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Stopped(recorder, project);
            return 0;
        }
        catch (TftpBindException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.Message);

            if (ex.Remediation is { } remediation)
            {
                Console.Error.WriteLine(remediation);
            }

            return 1;
        }
    }

    /// <summary>
    /// The last thing printed, and it says what was kept. A count of zero rows against a session
    /// somebody remembers seeing requests in is a finding on its own.
    /// </summary>
    private static void Stopped(TftpEventRecorder? recorder, ProjectStore? project)
    {
        Console.WriteLine();

        if (recorder is not null && project is not null)
        {
            Console.WriteLine(
                $"Recorded {recorder.RecordedCount.ToString(CultureInfo.InvariantCulture)} rows to "
                + $"{project.FilePath}.");

            if (recorder.FailureCount > 0)
            {
                Console.WriteLine(
                    $"** {recorder.FailureCount.ToString(CultureInfo.InvariantCulture)} rows could NOT be "
                    + "written. The record is incomplete; keep the transcript.");
            }
        }

        Console.WriteLine("Stopped. Start the real TFTP server again before you leave.");
    }

    /// <summary>
    /// Sends one write request and reports what answered.
    ///
    /// <para><b>It never sends a single byte of file data.</b> The first exchange is where the
    /// diagnostic value is - whether the request is accepted at all, which options survive, and
    /// crucially <i>what address and port the answer comes from</i>, because a TFTP server answers
    /// from a fresh ephemeral port and that is the step a stateful firewall or NAT breaks. After
    /// the answer it sends an error to tear the transfer down rather than leaving the server
    /// waiting.</para>
    ///
    /// <para>Point it at the watch in another window to prove the whole path end to end without a
    /// robot. Point it at a real server and it is the first half of PLAN-TFTP.md's part E4.</para>
    /// </summary>
    private static async Task<int> SendAsync(string host, Options options)
    {
        if (!TryResolve(host, out IPAddress? address))
        {
            Console.Error.WriteLine($"Could not resolve '{host}' to an IPv4 address.");
            return 2;
        }

        var destination = new IPEndPoint(address!, options.Port);
        TftpOptions offered = options.BuildOffer();

        Console.WriteLine($"Sending a write request to {destination}");
        Console.WriteLine($"  filename : '{options.File}'");
        Console.WriteLine($"  mode     : {TftpPacket.ModeToken(options.Mode)}");
        Console.WriteLine($"  options  : {offered}");
        Console.WriteLine("  No file data will be sent. This is the first exchange only.");

        if (!string.Equals(options.File, DefaultProbeFile, StringComparison.Ordinal))
        {
            Console.WriteLine();
            Console.WriteLine("  ** A server that accepts this request usually creates that name in its root");
            Console.WriteLine("     BEFORE any data arrives, so an existing file of that name can be truncated.");
            Console.WriteLine("     You changed it from the default - be sure it is not a backup somebody wants.");
        }

        Console.WriteLine();

        byte[] request = TftpPacket.EncodeRequest(TftpOpcode.WriteRequest, options.File, options.Mode, offered);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // Bound deliberately when asked. A plant laptop carries a dozen adapters - VMware, Hyper-V,
        // VirtualBox, a docking NIC - and the route table alone decides which one a datagram leaves
        // by. --from is the difference between probing the robot network and probing a virtual
        // switch, and the answer looks identical either way until you read the source address.
        if (options.From is { } localHost)
        {
            if (!TryResolve(localHost, out IPAddress? local))
            {
                Console.Error.WriteLine($"Could not resolve '{localHost}' to an IPv4 address.");
                return 2;
            }

            try
            {
                socket.Bind(new IPEndPoint(local!, 0));
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"Cannot send from {local}: {ex.SocketErrorCode} - {ex.Message}");
                Console.Error.WriteLine("It has to be an address this machine actually holds - --list shows them.");
                return 2;
            }
        }

        socket.SendTo(request, destination);

        Console.WriteLine($"Sent from {socket.LocalEndPoint}, which is this probe's transfer identifier.");
        Console.WriteLine();

        var buffer = new byte[TftpPacket.MaxDatagramLength];
        using var timeout = new CancellationTokenSource(options.Timeout);

        SocketReceiveFromResult result;
        try
        {
            result = await socket
                .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Nothing answered within {options.Timeout.TotalSeconds} seconds.");
            Console.WriteLine();
            Console.WriteLine("  Four things produce exactly this, and they need different people to fix:");
            Console.WriteLine("   - nothing is listening on that port over there (is the TFTP service running?)");
            Console.WriteLine("   - the server's firewall drops inbound UDP/69");
            Console.WriteLine("   - there is no route: try 'ping' first, and --from to leave by the right adapter");
            Console.WriteLine("   - the answer WAS sent and was dropped coming back. TFTP answers from a fresh");
            Console.WriteLine("     port, not from 69, so a firewall or NAT between here and there that does not");
            Console.WriteLine("     track TFTP eats the reply while the request itself got through. That one is");
            Console.WriteLine("     the whole reason this probe reports the source port it was answered from.");
            return 1;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"The send or receive failed: {ex.SocketErrorCode} - {ex.Message}");
            return 1;
        }

        // Parsing is span-based and Span<T> locals are illegal in async methods, so the decode and
        // the reporting happen in a plain synchronous helper - the same split the watch uses.
        bool accepted = ReportAnswer(buffer, result, destination, offered);

        if (accepted && result.RemoteEndPoint is IPEndPoint responder)
        {
            // Tear the transfer down rather than leaving the server waiting on data that is never
            // coming. A well-behaved server abandons it here.
            socket.SendTo(
                TftpPacket.EncodeError(TftpErrorCode.NotDefined, "NetControl probe: first exchange only, aborting."),
                responder);

            // "Nothing was written" would be an over-claim: no file DATA was sent, but a server
            // that accepts a write request generally creates the file first and may leave it empty.
            Console.WriteLine("Sent an error to abort the transfer. No file data was sent - but a server that");
            Console.WriteLine($"accepted the request may have left '{options.File}' in its root, probably empty.");
        }

        return 0;
    }

    /// <summary>Decodes and explains the answer. Returns true when a transfer was actually opened.</summary>
    private static bool ReportAnswer(
        byte[] buffer,
        SocketReceiveFromResult result,
        IPEndPoint destination,
        TftpOptions offered)
    {
        var responder = result.RemoteEndPoint as IPEndPoint;
        Console.WriteLine($"Answered by {responder}");

        if (responder is not null)
        {
            if (!responder.Address.Equals(destination.Address))
            {
                Console.WriteLine(
                    "  ** The answer came from a DIFFERENT ADDRESS than the request went to. A correct");
                Console.WriteLine(
                    "     TFTP client discards that as an unknown transfer id, and the transfer stalls with");
                Console.WriteLine(
                    "     no error anywhere. This is what a multi-homed server does. Worth chasing.");
            }
            else if (responder.Port != destination.Port)
            {
                Console.WriteLine(
                    $"  The answer came from port {responder.Port.ToString(CultureInfo.InvariantCulture)} rather "
                    + $"than {destination.Port.ToString(CultureInfo.InvariantCulture)}, which is correct: a");
                Console.WriteLine(
                    "  transfer moves to a fresh port. It is also the step a stateful firewall or NAT breaks.");
            }
        }

        if (!TftpPacket.TryParse(buffer.AsSpan(0, result.ReceivedBytes), out TftpMessage? message, out string? problem)
            || message is null)
        {
            Console.WriteLine($"  The answer could not be read as TFTP: {problem}");
            return false;
        }

        Console.WriteLine($"  {message.Describe()}");

        switch (message)
        {
            case TftpErrorMessage error:
                (string summary, string? remediation) = error.Explain();
                Console.WriteLine();
                Console.WriteLine($"  {summary}");
                if (remediation is not null)
                {
                    Console.WriteLine($"  {remediation}");
                }

                return false;

            case TftpOptionAckMessage oack:
                Console.WriteLine();
                Console.WriteLine("  The request was accepted and options were negotiated.");
                ReportOptionGap(offered, oack.Options);
                return true;

            case TftpAckMessage { IsWriteAccepted: true }:
                Console.WriteLine();
                Console.WriteLine("  The request was accepted.");
                ReportOptionGap(offered, TftpOptions.None);
                return true;

            default:
                return false;
        }
    }

    private static void ReportOptionGap(TftpOptions requested, TftpOptions granted)
    {
        IReadOnlyList<string> differences = TftpOptions.DescribeDifferences(requested, granted);

        if (differences.Count == 0)
        {
            if (!requested.IsEmpty)
            {
                Console.WriteLine("  Every option offered was granted as asked.");
            }

            return;
        }

        // The gap between requested and granted is the half no server log records.
        foreach (string difference in differences)
        {
            Console.WriteLine($"  OPTION   : {difference}");
        }
    }

    private static bool TryResolve(string host, out IPAddress? address)
    {
        if (IPAddress.TryParse(host, out address) && address.AddressFamily == AddressFamily.InterNetwork)
        {
            return true;
        }

        try
        {
            address = Array.Find(
                Dns.GetHostAddresses(host),
                candidate => candidate.AddressFamily == AddressFamily.InterNetwork);

            return address is not null;
        }
        catch (SocketException)
        {
            address = null;
            return false;
        }
        catch (ArgumentException)
        {
            address = null;
            return false;
        }
    }

    /// <summary>
    /// The lines the whole exercise is for. Everything a controller told us about what it wants,
    /// laid out so it can be copied into a notebook rather than retyped from a scrolling log.
    /// </summary>
    private static void PrintRequest(TftpRequestEventArgs e)
    {
        string when = e.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string repeat = e.IsRetransmit ? "  (retransmit)" : string.Empty;

        Console.WriteLine($"{when}  {e.Describe()}{repeat}");
        Console.WriteLine($"          filename : '{e.Request.FileName}'");
        Console.WriteLine($"          mode     : {e.Request.RawMode}");
        Console.WriteLine($"          options  : {e.Request.Options}");
        Console.WriteLine($"          from     : {e.Source}");
        Console.WriteLine($"          we did   : {e.Action} - {e.Reason}");

        foreach (string concern in e.Request.Concerns())
        {
            Console.WriteLine($"          NOTE     : {concern}");
        }

        Console.WriteLine();
    }

    private static void PrintFault(TftpFaultEventArgs e)
    {
        string when = e.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        Console.WriteLine($"{when}  {(e.IsFatal ? "FATAL" : "note")}: {e.Message}");

        if (e.Remediation is { } remediation)
        {
            Console.WriteLine($"          {remediation}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Everything the interface bar would say, before a packet arrives. Both Inspect calls answer
    /// honestly on a platform they cannot query, so neither needs an OS guard here.
    /// </summary>
    private static void ReportEnvironment(INicInventory nics, Options options)
    {
        Console.WriteLine("Adapters");
        foreach (NicInfo nic in nics.Snapshot())
        {
            string address = nic.IPv4?.ToString() ?? "no address";
            Console.WriteLine($"  [{nic.Index,3}] {nic.Name,-28} {address,-16} {nic.Status}");
        }

        Console.WriteLine();

        PortConflictReport conflict = PortConflictDetector.Inspect(options.Port);
        Console.WriteLine($"UDP/{options.Port}: {conflict.Summary}");
        WriteIndented(conflict.Remediation);

        if (!conflict.IsClear)
        {
            WriteIndented(
                "On a backup server that is expected - the process holding this port is the TFTP server "
                + "itself. Stop it deliberately before watching, and start it again after.");
        }

        FirewallStatus firewall = FirewallCheck.Inspect(options.Port);
        Console.WriteLine($"Firewall: {firewall.Summary}");
        WriteIndented(firewall.Remediation);

        if (options.Root is { } root)
        {
            TftpRootStatus status = TftpRootCheck.Inspect(root);
            Console.WriteLine($"Root: {status.Summary}");
            WriteIndented(status.Remediation);
        }

        Console.WriteLine();
    }

    private static void WriteIndented(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine($"  {text}");
        }
    }

    private static void Usage() => Console.WriteLine("""
        tftp-spike - watch UDP/69, or ask a TFTP server for something and report what comes back.

        Watching (the default):
          --list            Report the adapters, who owns the port, and the firewall, then stop.
          --nic <index>     Only act on requests arriving on this interface index. Requests on
                            other adapters are still printed, and never answered.
          --port <n>        Listen somewhere other than 69, to try this without taking the port
                            the real server needs.
          --root <path>     Grade a TFTP root folder: does it exist, can this account write into
                            it, is there room. A CHECK ONLY - the watch never stores anything,
                            there or anywhere else.
          --quiet           Transmit nothing at all, not even a refusal. The controller will then
                            retransmit until it times out.
          --refuse-from-ephemeral
                            Send each refusal from a fresh socket, so it carries a transfer
                            identifier of its own instead of arriving from the well-known port.
                            Try this if the controller ignores an ordinary refusal and keeps
                            retransmitting - it is the one thing that cannot be worked out from
                            a specification, only from a controller.

          It never accepts a transfer. Every request is recorded and refused.

        Keeping what you saw. Do not run a session in a plant without at least one of these:
          --log <file>      Mirror everything printed to a file, flushed line by line, appended
                            rather than overwritten. This is what a person read.
          --project <file>  Record into a NetControl project file: one append-only row per
                            request, refusal and fault, which no later edit can change. This is
                            the record. It refuses to start if the file cannot be opened.
          --record-retransmits
                            Also record repeats. Off by default because identical rows bury the
                            first one, which is the row carrying the filename. Turn it on when
                            the question is specifically how this controller retries.

        Asking (--send), which is how you exercise any of this without a robot:
          --send <host>     Send ONE write request and report what answered - which address and
                            port it came from, which options survived, and what any error means.
                            No file data is ever sent, and the transfer is aborted afterwards.
          --from <address>  Send from this local address. Worth being explicit about on a laptop
                            carrying VMware, Hyper-V and a docking NIC.
          --file <name>     The filename to ask for. Default is obviously a probe.
          --mode <m>        octet (default) or netascii.
          --blksize <n>     Offer a block size. Try 1468.
          --tsize <n>       Offer a transfer size.
          --timeout <s>     How long to wait for the answer. Default 5.

        Getting the packets to arrive at all:
          --firewall        Print the exact elevated netsh command that lets inbound UDP/69 reach
                            THIS executable, and the one that removes it again. A rule names an
                            exe, so publish one that stays put first:
                            pwsh tools/publish-tftp-spike.ps1

        To try the whole path with no robot and no real server, run the watch in one window and
        --send at it from another:

          dotnet run --project spikes/Spike3.TftpWatch -- --port 6969
          dotnet run --project spikes/Spike3.TftpWatch -- --send 127.0.0.1 --port 6969

        BENCH.md Run 6b is the procedure for doing this against a robot, and it stops the plant's
        TFTP server.
        """);

    private sealed record Options
    {
        public int Port { get; init; } = TftpLimits.ServerPort;

        public int? InterfaceIndex { get; init; }

        public string? Root { get; init; }

        public bool Quiet { get; init; }

        public bool ListOnly { get; init; }

        /// <summary>Where to mirror everything printed. Null means the console is the only copy.</summary>
        public string? LogPath { get; init; }

        /// <summary>The project file to record into - the append-only half. Null means no record.</summary>
        public string? ProjectPath { get; init; }

        /// <summary>
        /// Write a row for retransmits too. Off by default, as on the DHCP side, because identical
        /// rows bury the first one - and the first one carries the filename. Worth turning on for
        /// exactly one question: how this controller retries when it is refused.
        /// </summary>
        public bool RecordRetransmits { get; init; }

        /// <summary>Answer from a fresh socket rather than from the well-known port.</summary>
        public bool RefuseFromEphemeral { get; init; }

        /// <summary>Print the firewall command for this executable and stop.</summary>
        public bool ShowFirewallCommand { get; init; }

        public string? SendTo { get; init; }

        /// <summary>Local address to send the probe from, on a machine with more than one.</summary>
        public string? From { get; init; }

        public string File { get; init; } = DefaultProbeFile;

        public TftpTransferMode Mode { get; init; } = TftpTransferMode.Octet;

        public int? BlockSize { get; init; }

        public long? TransferSize { get; init; }

        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

        public TftpOptions BuildOffer()
        {
            var offered = new List<TftpOption>();

            if (BlockSize is { } blockSize)
            {
                offered.Add(new TftpOption(
                    TftpOption.BlockSizeName,
                    blockSize.ToString(CultureInfo.InvariantCulture)));
            }

            if (TransferSize is { } transferSize)
            {
                offered.Add(new TftpOption(
                    TftpOption.TransferSizeName,
                    transferSize.ToString(CultureInfo.InvariantCulture)));
            }

            return offered.Count == 0 ? TftpOptions.None : new TftpOptions(offered);
        }

        // A switch statement rather than a switch expression: most of these arms advance the loop
        // variable by ref, and burying that inside an expression would be clever at the cost of
        // being obvious.
        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--list":
                        options = options with { ListOnly = true };
                        break;

                    case "--quiet":
                        options = options with { Quiet = true };
                        break;

                    case "--log":
                        options = options with { LogPath = Next(args, ref i, "--log") };
                        break;

                    case "--project":
                        options = options with { ProjectPath = Next(args, ref i, "--project") };
                        break;

                    case "--record-retransmits":
                        options = options with { RecordRetransmits = true };
                        break;

                    case "--refuse-from-ephemeral":
                        options = options with { RefuseFromEphemeral = true };
                        break;

                    case "--firewall":
                        options = options with { ShowFirewallCommand = true };
                        break;

                    case "--from":
                        options = options with { From = Next(args, ref i, "--from") };
                        break;

                    case "--nic":
                        options = options with { InterfaceIndex = NextInt(args, ref i, "--nic") };
                        break;

                    case "--port":
                        options = options with { Port = NextInt(args, ref i, "--port") };
                        break;

                    case "--root":
                        options = options with { Root = Next(args, ref i, "--root") };
                        break;

                    case "--send":
                        options = options with { SendTo = Next(args, ref i, "--send") };
                        break;

                    case "--file":
                        options = options with { File = Next(args, ref i, "--file") };
                        break;

                    case "--mode":
                        options = options with { Mode = ParseMode(Next(args, ref i, "--mode")) };
                        break;

                    case "--blksize":
                        options = options with { BlockSize = NextInt(args, ref i, "--blksize") };
                        break;

                    case "--tsize":
                        options = options with { TransferSize = NextInt(args, ref i, "--tsize") };
                        break;

                    case "--timeout":
                        options = options with
                        {
                            Timeout = TimeSpan.FromSeconds(NextInt(args, ref i, "--timeout")),
                        };
                        break;

                    default:
                        throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
                }
            }

            return options;
        }

        private static TftpTransferMode ParseMode(string raw)
        {
            TftpTransferMode mode = TftpPacket.ParseMode(raw);

            return mode == TftpTransferMode.Unknown
                ? throw new ArgumentException($"'{raw}' is not a transfer mode. Use octet or netascii.")
                : mode;
        }

        private static string Next(string[] args, ref int i, string name) =>
            i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value.");

        private static int NextInt(string[] args, ref int i, string name) =>
            int.TryParse(Next(args, ref i, name), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                ? value
                : throw new ArgumentException($"{name} needs a whole number.");
    }
}
