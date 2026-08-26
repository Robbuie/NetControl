using NetControl.Core.Oui;
using NetControl.OuiPacker;

// Refreshing the OUI table is a chore with a reviewable diff, not a build step. Nothing in src/
// runs this; you run it, look at what it printed, rebuild, and commit the changed oui.bin.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Commands.PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;              // let the download unwind rather than leaving a half-written file
    cancellation.Cancel();
};

try
{
    var options = new CommandLine(args);

    return args[0] switch
    {
        "update" => await Commands.UpdateAsync(options, cancellation.Token).ConfigureAwait(false),
        "fetch" => await Commands.FetchAsync(options, cancellation.Token).ConfigureAwait(false),
        "pack" => Commands.Pack(options),
        "dump" => Commands.Dump(options),
        _ => Unknown(args[0]),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException
                              or FormatException or OuiException or HttpRequestException)
{
    // Everything this tool raises deliberately. Anything else is a bug and should keep its stack.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    Console.Error.WriteLine();
    Commands.PrintUsage();
    return 2;
}
