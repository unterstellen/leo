using Leo.Core.Networking;

namespace Leo.Tests;

public sealed class WifiPinPolicyTests
{
    private static readonly string[] Pins = ["Zixel"];

    [Fact]
    public void On_A_Pinned_Network_Nothing_Happens() =>
        Assert.Equal(PinAction.Nothing, WifiPinPolicy.Decide("Zixel", WifiConnectionMode.Profile, Pins, isPrimary: true, null));

    [Fact]
    public void No_Pins_Nothing_Happens() =>
        Assert.Equal(PinAction.Nothing, WifiPinPolicy.Decide("Other", WifiConnectionMode.Auto, [], isPrimary: true, null));

    [Fact]
    public void A_Drop_Is_Corrected() =>
        Assert.Equal(PinAction.Enforce, WifiPinPolicy.Decide(null, WifiConnectionMode.Unknown, Pins, isPrimary: true, null));

    [Fact]
    public void Windows_Moving_It_By_Itself_Is_Corrected() =>
        Assert.Equal(PinAction.Enforce, WifiPinPolicy.Decide("Cafe", WifiConnectionMode.Auto, Pins, isPrimary: true, null));

    [Fact]
    public void A_Manual_Switch_On_The_Primary_Becomes_The_Pin()
    {
        Assert.Equal(PinAction.AdoptAsPin, WifiPinPolicy.Decide("Office", WifiConnectionMode.Profile, Pins, isPrimary: true, "Zixel"));
        Assert.Equal(["Office", "Zixel"], WifiPinPolicy.Adopt(Pins, "Office"));
        Assert.Equal(["Zixel", "Office"], WifiPinPolicy.Adopt(["Office", "Zixel"], "Zixel"));
    }

    [Fact]
    public void A_Manual_Switch_On_Another_Adapter_Is_Left_Alone() =>
        Assert.Equal(PinAction.Hold, WifiPinPolicy.Decide("Office", WifiConnectionMode.Profile, Pins, isPrimary: false, null));

    [Fact]
    public void LEOs_Own_Reconnect_Is_Not_Mistaken_For_The_User() =>
        Assert.Equal(PinAction.Enforce, WifiPinPolicy.Decide("Old", WifiConnectionMode.Profile, ["New"], isPrimary: true, "Old"));
}
