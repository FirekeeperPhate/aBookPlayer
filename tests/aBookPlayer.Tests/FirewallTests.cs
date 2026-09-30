namespace aBookPlayer.Tests;

public class FirewallTests
{
    const string App = @"C:\Program Files\aBookPlayer\aBookPlayer.exe";
    const int Domain = 1, Private = 2, Public = 4, Tcp = 6, Udp = 17, Any = 256;

    static Firewall.Rule ForApp(int profiles, int protocol, bool block = false) => new(profiles, protocol, App, null, "*", block);

    static Firewall.State Decide(int current, params Firewall.Rule[] rules) => Firewall.Decide(rules, current, current, App, 52780);

    [Fact]
    public void Windows_answer_for_private_networks_only_lets_phones_in_at_home()
    {
        // What Windows adds when its question is answered with "private networks" ticked
        Firewall.Rule[] rules = [ForApp(Private, Tcp), ForApp(Private, Udp), ForApp(Public, Tcp, block: true), ForApp(Public, Udp, block: true)];
        // Home Wi-Fi (private) and Hyper-V's switch (public) at once: phones at home get through
        Assert.Equal(Firewall.State.Allowed, Decide(Private | Public, rules));
        // Only on a public network: blocked
        Assert.Equal(Firewall.State.Blocked, Decide(Public, rules));
    }

    [Fact]
    public void A_block_rule_wins_on_its_networks()
    {
        Assert.Equal(Firewall.State.Blocked, Decide(Private, ForApp(Private | Public, Any), ForApp(Private, Tcp, block: true)));
        Assert.Equal(Firewall.State.Allowed, Decide(Private, ForApp(Private | Public, Any)));
        Assert.Equal(Firewall.State.NoRule, Decide(Private, new Firewall.Rule(Private, Tcp, @"C:\Other\other.exe", null, "*", false)));
    }

    [Fact]
    public void A_port_rule_for_TCP_only_lets_phones_connect_but_not_find_the_PC()
    {
        var tcpPort = new Firewall.Rule(Private, Tcp, null, null, "52780", false);
        Assert.Equal(Firewall.State.NoDiscovery, Decide(Private, tcpPort));
        Assert.Equal(Firewall.State.Allowed, Decide(Private, tcpPort, new Firewall.Rule(Private, Udp, null, null, "52000-53000", false)));
        // A Windows service's rule for every port is not this app's
        Assert.Equal(Firewall.State.NoRule, Decide(Private, new Firewall.Rule(Private | Domain, Tcp, null, "RemoteRegistry", "*", false)));
    }

    [Fact]
    public void The_firewall_off_on_a_network_lets_phones_in()
    {
        Assert.Equal(Firewall.State.Allowed, Firewall.Decide([ForApp(Public, Any, block: true)], Private | Public, firewallOn: Public, App, 52780));
    }
}
