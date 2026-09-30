using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace aBookPlayer;

/// <summary>
/// Whether Windows Firewall lets phones reach the library shared by this app, and a rule that lets them: inbound,
/// for this program, from local network addresses only (not from the internet).
/// </summary>
static class Firewall
{
    /// <summary>
    /// Allowed: phones connect and find the PC. NoDiscovery: they connect (TCP), but their search (UDP) is blocked,
    /// so the address has to be typed. Blocked: they cannot connect. NoRule: Windows decides (it may ask, or block).
    /// </summary>
    public enum State { Allowed, NoDiscovery, Blocked, NoRule, Off, Unknown }

    /// <summary>An inbound rule's fields that matter here (INetFwRule).</summary>
    internal sealed record Rule(int Profiles, int Protocol, string? Application, string? Service, string? LocalPorts, bool Block);

    const string RuleName = "aBookPlayer";
    // Home and office networks: the private address ranges and the network the PC is on
    const string LocalNetworks = "LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16,fe80::/10";
    const int Inbound = 1, Block = 0, Tcp = 6, Udp = 17, AnyProtocol = 256;
    static readonly int[] Profiles = [1, 2, 4]; // domain, private, public

    /// <summary>
    /// What the firewall does with connections to <paramref name="port"/> (TCP) and the phones' search (UDP) of
    /// <paramref name="program"/>, from its enabled inbound rules for the current networks (reading them needs no
    /// administrator rights). Slow: a few hundred ms.
    /// </summary>
    public static State Check(string program, int port)
    {
        try
        {
            if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } type) return State.Unknown;
            dynamic policy = Activator.CreateInstance(type)!;
            int current = policy.CurrentProfileTypes;
            int on = 0;
            foreach (int profile in Profiles)
                if ((current & profile) != 0 && (bool)policy.FirewallEnabled[profile]) on |= profile;
            if (on == 0) return State.Off;

            var rules = new List<Rule>();
            foreach (dynamic rule in policy.Rules)
            {
                if (!(bool)rule.Enabled || (int)rule.Direction != Inbound) continue;
                string? application = rule.ApplicationName;
                rules.Add(new Rule((int)rule.Profiles, (int)rule.Protocol, application is { Length: > 0 } ? Environment.ExpandEnvironmentVariables(application) : null,
                    rule.ServiceName as string, rule.LocalPorts as string, (int)rule.Action == Block));
            }
            return Decide(rules, current, on, program, port);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            return State.Unknown;
        }
    }

    /// <summary>
    /// The state from the rules, network by network: phones are on one of the PC's networks (the home one, most
    /// likely private), so one network letting them through is enough, even if another (a VPN, Hyper-V's switch,
    /// usually public) blocks them. A block rule wins over allow rules on its networks.
    /// </summary>
    internal static State Decide(IReadOnlyList<Rule> rules, int currentProfiles, int firewallOn, string program, int port)
    {
        bool anyConnects = false, anyBlocked = false;
        foreach (int profile in Profiles)
        {
            if ((currentProfiles & profile) == 0) continue;
            // The firewall off on this network: nothing is stopped there
            if ((firewallOn & profile) == 0) return State.Allowed;
            var tcp = Verdict(rules, profile, Tcp, program, port);
            var udp = Verdict(rules, profile, Udp, program, LibraryServer.DiscoveryPort);
            if (tcp == true && udp == true) return State.Allowed;
            anyConnects |= tcp == true;
            anyBlocked |= tcp == false;
        }
        return anyConnects ? State.NoDiscovery : anyBlocked ? State.Blocked : State.NoRule;
    }

    /// <summary>On one network, for one protocol: allowed (true), blocked (false), or no rule (null).</summary>
    static bool? Verdict(IReadOnlyList<Rule> rules, int profile, int protocol, string program, int port)
    {
        bool? verdict = null;
        foreach (var rule in rules)
        {
            if ((rule.Profiles & profile) == 0 || (rule.Protocol != protocol && rule.Protocol != AnyProtocol)) continue;
            bool mine = rule.Application == null
                // A rule for a port (not for a program, nor for a Windows service)
                ? rule.Protocol == protocol && rule.Service is null or "" or "*" && HasPort(rule.LocalPorts, port)
                : string.Equals(rule.Application, program, StringComparison.OrdinalIgnoreCase);
            if (!mine) continue;
            if (rule.Block) return false;
            verdict = true;
        }
        return verdict;
    }

    /// <summary>"80,52780-52790": whether <paramref name="port"/> is one of them ("*" is every port).</summary>
    static bool HasPort(string? ports, int port) =>
        ports != null && ports.Split(',', StringSplitOptions.TrimEntries).Any(p =>
            p == "*" || (p.Split('-') is [var from, var to] ? int.TryParse(from, out int a) && int.TryParse(to, out int b) && a <= port && port <= b
                                                            : int.TryParse(p, out int single) && single == port));

    /// <summary>
    /// Replaces the program's inbound rules (also the "block" ones Windows makes when its question is dismissed) with
    /// one allowing it from local networks. Asks for administrator rights; false when refused or failed.
    /// </summary>
    public static async Task<bool> AllowAsync(string program)
    {
        string delete = $"netsh advfirewall firewall delete rule name=all dir=in program=\"{program}\"";
        string add = $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{program}\" enable=yes profile=any remoteip={LocalNetworks}";
        try
        {
            // cmd removes the outer quotes and keeps the program's
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{delete} & {add}\"")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // The administrator question was answered no
            return false;
        }
    }
}
