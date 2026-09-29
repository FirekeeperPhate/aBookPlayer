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
    public enum State { Allowed, Blocked, NoRule, Off, Unknown }

    const string RuleName = "aBookPlayer";
    // Home and office networks: the private address ranges and the network the PC is on
    const string LocalNetworks = "LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16,fe80::/10";
    const int Inbound = 1, Block = 0, Tcp = 6, AnyProtocol = 256;

    /// <summary>
    /// What the firewall does with connections to <paramref name="port"/> of <paramref name="program"/>, from its
    /// enabled inbound rules for the current networks (reading them needs no administrator rights). Slow: a few hundred ms.
    /// </summary>
    public static State Check(string program, int port)
    {
        try
        {
            if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } type) return State.Unknown;
            dynamic policy = Activator.CreateInstance(type)!;
            int profiles = policy.CurrentProfileTypes;
            bool on = false;
            foreach (int profile in new[] { 1, 2, 4 })
                if ((profiles & profile) != 0 && (bool)policy.FirewallEnabled[profile]) on = true;
            if (!on) return State.Off;

            bool allowed = false;
            foreach (dynamic rule in policy.Rules)
            {
                if (!(bool)rule.Enabled || (int)rule.Direction != Inbound || ((int)rule.Profiles & profiles) == 0) continue;
                int protocol = rule.Protocol;
                if (protocol is not (Tcp or AnyProtocol)) continue;
                string? application = rule.ApplicationName;
                bool mine = string.IsNullOrEmpty(application)
                    ? protocol == Tcp && HasPort(rule.LocalPorts as string, port)
                    : string.Equals(Environment.ExpandEnvironmentVariables(application), program, StringComparison.OrdinalIgnoreCase);
                if (!mine) continue;
                // A block rule wins over any allow rule
                if ((int)rule.Action == Block) return State.Blocked;
                allowed = true;
            }
            return allowed ? State.Allowed : State.NoRule;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            return State.Unknown;
        }
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
