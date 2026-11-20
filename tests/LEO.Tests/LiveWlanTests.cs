using Leo.Service.Interop;
using Xunit.Abstractions;

namespace Leo.Tests;

/// <summary>Read-only Native Wifi checks against real hardware. Only with LEO_LIVE_TESTS=1.</summary>
public sealed class LiveWlanTests(ITestOutputHelper output)
{
    [Fact]
    public void Lists_Interfaces_Connection_And_Networks()
    {
        if (Environment.GetEnvironmentVariable("LEO_LIVE_TESTS") != "1") return;

        using var wlan = new WlanApi();
        var interfaces = wlan.GetInterfaces();
        Assert.NotEmpty(interfaces);
        foreach (var i in interfaces)
        {
            output.WriteLine($"{i.Id:B} state={i.State} connected={wlan.GetConnectedSsid(i.Id) ?? "-"} '{i.Description}'");
            output.WriteLine($"   profiles: {string.Join(", ", wlan.GetProfileNames(i.Id))}");
            foreach (var n in wlan.GetAvailableNetworks(i.Id).OrderByDescending(n => n.SignalPercent).Take(8))
            {
                output.WriteLine($"   {n.Ssid,-28} {n.SignalPercent,3}% auth={n.AuthAlgorithm} cipher={n.CipherAlgorithm} " +
                                 $"sec={n.SecurityEnabled} conn={n.IsConnected} profile={n.HasProfile}('{n.ProfileName}') connectable={n.Connectable}");
            }
        }
        Assert.Contains(interfaces, i => wlan.GetConnectedSsid(i.Id) is { Length: > 0 });
    }
}
