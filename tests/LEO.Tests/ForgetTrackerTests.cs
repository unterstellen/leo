using Leo.Service.Wifi;

namespace Leo.Tests;

public sealed class ForgetTrackerTests
{
    private static readonly Guid WiFi = Guid.NewGuid();
    private static readonly Guid WiFi2 = Guid.NewGuid(); // hidden secondary station
    private static readonly Guid Usb = Guid.NewGuid();   // USB Wi-Fi dongle

    private static Dictionary<Guid, ProfileSnapshot> State(params (Guid Iface, bool Secondary, string[] Ssids)[] items) =>
        items.ToDictionary(i => i.Iface, i => new ProfileSnapshot(i.Secondary, i.Ssids.ToDictionary(s => s, s => s)));

    [Fact]
    public void First_Snapshot_Never_Reports_Forgets()
    {
        var t = new ForgetTracker();
        Assert.Empty(t.Update(State((WiFi, false, ["Zixel"]))));
    }

    [Fact]
    public void Removal_On_A_Primary_Is_A_User_Forget()
    {
        var t = new ForgetTracker();
        t.Update(State((WiFi, false, ["Zixel", "Cafe"]), (WiFi2, true, ["Zixel"])));
        var forgotten = t.Update(State((WiFi, false, ["Cafe"]), (WiFi2, true, ["Zixel"])));
        Assert.Equal(["Zixel"], forgotten);
    }

    [Fact]
    public void Removal_On_A_Hidden_Secondary_Is_Not_A_User_Forget()
    {
        var t = new ForgetTracker();
        t.Update(State((WiFi, false, ["Zixel"]), (WiFi2, true, ["Zixel"])));
        Assert.Empty(t.Update(State((WiFi, false, ["Zixel"]), (WiFi2, true, []))));
    }

    [Fact]
    public void Unplugged_Adapter_Is_Not_A_Forget()
    {
        var t = new ForgetTracker();
        t.Update(State((WiFi, false, ["Zixel"]), (Usb, false, ["Hotel"])));
        Assert.Empty(t.Update(State((WiFi, false, ["Zixel"]))));
    }

    [Fact]
    public void Leos_Own_Deletes_Are_Not_Mistaken_For_User_Forgets()
    {
        var t = new ForgetTracker();
        t.Update(State((WiFi, false, ["Zixel"]), (Usb, false, ["Zixel"])));

        // User forgets on WiFi → LEO deletes the copy on the USB dongle, marking it as its own.
        Assert.Equal(["Zixel"], t.Update(State((WiFi, false, []), (Usb, false, ["Zixel"]))));
        t.Suppress(Usb, "Zixel");
        Assert.Empty(t.Update(State((WiFi, false, []), (Usb, false, []))));
    }

    [Fact]
    public void Added_Profiles_Are_Ignored()
    {
        var t = new ForgetTracker();
        t.Update(State((WiFi, false, ["Zixel"])));
        Assert.Empty(t.Update(State((WiFi, false, ["Zixel", "New"]))));
    }
}
