using System.Globalization;
using System.Runtime.Versioning;

namespace NetControl.Core.Interfaces;

/// <summary>
/// Reads the Windows Firewall configuration to answer one question: will an inbound UDP/67
/// datagram reach this executable?
///
/// It reads only. Creating a rule needs elevation, and the finding from Phase 0 that binding
/// UDP/67 works as a standard user is worth protecting — a tool you can copy to a plant laptop
/// and run is a different product from one that needs a ticket raised with IT. So when a rule is
/// missing we hand the user the exact elevated command rather than demanding elevation ourselves.
///
/// Everything here is best-effort. A firewall check that throws is worse than one that says
/// "unknown", so every failure path lands on <see cref="FirewallVerdict.Unknown"/>.
/// </summary>
public static class FirewallCheck
{
    private const int NetFwIpProtocolUdp = 17;
    private const int NetFwIpProtocolAny = 256;
    private const int NetFwRuleDirIn = 1;
    private const int NetFwActionAllow = 1;
    private const int ProfileAll = 0x7FFFFFFF;

    /// <summary>NET_FW_PROFILE_TYPE2: Domain, Private, Public.</summary>
    private static readonly int[] ProfileTypes = [1, 2, 4];

    /// <summary>
    /// Inspects inbound rules for <paramref name="port"/> as they apply to this executable.
    /// </summary>
    public static FirewallStatus Inspect(int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FirewallStatus.Unknown(port, "not running on Windows");
        }

        return InspectWindows(port);
    }

    /// <summary>
    /// The command that creates the missing rule. Handed to the user to run from an elevated
    /// prompt; shown in the UI so there is no guessing about what we would have done.
    /// </summary>
    public static string BuildAddRuleCommand(int port, string? executablePath = null)
    {
        string exe = executablePath ?? Environment.ProcessPath ?? "<path to this exe>";
        return $"""netsh advfirewall firewall add rule name="NetControl inbound UDP/{port}" dir=in action=allow protocol=UDP localport={port} program="{exe}" enable=yes""";
    }

    [SupportedOSPlatform("windows")]
    private static FirewallStatus InspectWindows(int port)
    {
        Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (policyType is null)
        {
            return FirewallStatus.Unknown(port, "the Windows Firewall COM component is not registered");
        }

        object? instance;
        try
        {
            instance = Activator.CreateInstance(policyType);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FirewallStatus.Unknown(port, ex.Message);
        }

        if (instance is null)
        {
            return FirewallStatus.Unknown(port, "the Windows Firewall COM component could not be created");
        }

        string exePath = Environment.ProcessPath ?? string.Empty;

        try
        {
            dynamic policy = instance;
            int activeProfiles = (int)policy.CurrentProfileTypes;

            // Assigned to a typed local first: the argument is dynamic, so the call is bound at
            // runtime and the result would otherwise stay dynamic all the way into the if.
            bool enforcing = AnyProfileEnforcing(policy, activeProfiles);

            if (!enforcing)
            {
                return new FirewallStatus(
                    FirewallVerdict.NotEnforced,
                    port,
                    [],
                    [],
                    "Windows Firewall is switched off for the active network profile, so inbound "
                        + $"UDP/{port} is not being filtered.",
                    null);
            }

            var allows = new List<string>();
            var blocks = new List<string>();

            foreach (dynamic rule in policy.Rules)
            {
                bool applies = Matches(rule, port, activeProfiles, exePath);
                if (!applies)
                {
                    continue;
                }

                string name = (string?)rule.Name ?? "(unnamed rule)";
                int action = (int)rule.Action;

                if (action == NetFwActionAllow)
                {
                    allows.Add(name);
                }
                else
                {
                    blocks.Add(name);
                }
            }

            return Grade(port, allows, blocks, exePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Late-bound COM throws a wide and poorly documented set of exceptions, and a policy
            // store that has been locked down by group policy is a normal thing to meet on a
            // plant laptop. Report it, do not propagate it.
            return FirewallStatus.Unknown(port, ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool AnyProfileEnforcing(dynamic policy, int activeProfiles)
    {
        foreach (int profile in ProfileTypes)
        {
            if ((activeProfiles & profile) == 0)
            {
                continue;
            }

            if ((bool)policy.FirewallEnabled[profile])
            {
                return true;
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool Matches(dynamic rule, int port, int activeProfiles, string exePath)
    {
        if (!(bool)rule.Enabled)
        {
            return false;
        }

        if ((int)rule.Direction != NetFwRuleDirIn)
        {
            return false;
        }

        int protocol = (int)rule.Protocol;
        if (protocol != NetFwIpProtocolUdp && protocol != NetFwIpProtocolAny)
        {
            return false;
        }

        int profiles = (int)rule.Profiles;
        if (profiles != ProfileAll && (profiles & activeProfiles) == 0)
        {
            return false;
        }

        if (!PortMatches((string?)rule.LocalPorts, port))
        {
            return false;
        }

        // A null ApplicationName means the rule covers any program, which does cover us.
        string? application = (string?)rule.ApplicationName;
        return string.IsNullOrEmpty(application)
            || (exePath.Length > 0 && application.Equals(exePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// LocalPorts is a comma-separated list that may contain "*", single ports, or ranges.
    /// Anything we cannot parse is treated as not matching, because claiming a rule covers the
    /// port when it might not is the failure that costs an hour on site.
    /// </summary>
    internal static bool PortMatches(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts))
        {
            return false;
        }

        foreach (string rawPart in localPorts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawPart == "*")
            {
                return true;
            }

            int dash = rawPart.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                if (int.TryParse(rawPart, CultureInfo.InvariantCulture, out int single) && single == port)
                {
                    return true;
                }

                continue;
            }

            if (int.TryParse(rawPart[..dash], CultureInfo.InvariantCulture, out int low)
                && int.TryParse(rawPart[(dash + 1)..], CultureInfo.InvariantCulture, out int high)
                && port >= low && port <= high)
            {
                return true;
            }
        }

        return false;
    }

    private static FirewallStatus Grade(int port, List<string> allows, List<string> blocks, string exePath)
    {
        if (blocks.Count > 0)
        {
            return new FirewallStatus(
                FirewallVerdict.Blocked,
                port,
                allows,
                blocks,
                $"Windows Firewall has an enabled block rule covering inbound UDP/{port} for this program "
                    + $"({string.Join(", ", blocks)}). Requests will be dropped with no prompt and no log entry.",
                $"Remove or disable that rule, then add an allow rule:{Environment.NewLine}"
                    + BuildAddRuleCommand(port, exePath));
        }

        if (allows.Count > 0)
        {
            return new FirewallStatus(
                FirewallVerdict.Allowed,
                port,
                allows,
                blocks,
                $"Inbound UDP/{port} is allowed by {string.Join(", ", allows)}.",
                null);
        }

        return new FirewallStatus(
            FirewallVerdict.NoRule,
            port,
            allows,
            blocks,
            $"No firewall rule allows inbound UDP/{port} for this program. Windows blocks unsolicited "
                + "inbound traffic by default, so requests will probably be dropped — though Windows may "
                + "prompt once when the socket first binds.",
            $"Allow it when prompted, or run this from an elevated prompt:{Environment.NewLine}"
                + BuildAddRuleCommand(port, exePath));
    }
}
