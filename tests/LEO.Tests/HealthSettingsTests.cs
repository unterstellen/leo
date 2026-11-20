using Leo.Core.Config;

namespace Leo.Tests;

public sealed class HealthSettingsTests
{
    [Fact]
    public void Defaults_Are_Valid() => Assert.Null(HealthSettingsValidator.Validate(new HealthSettings()));

    [Theory]
    [InlineData(1, 6, 30, 1000)]      // interval too short
    [InlineData(5, 0, 30, 1000)]      // failover delay too short
    [InlineData(5, 6, 4, 1000)]       // failback too short
    [InlineData(5, 6, 30, 50)]        // slow threshold too low
    public void Out_Of_Range_Timing_Is_Rejected(int interval, int failover, int failback, int slow) =>
        Assert.NotNull(HealthSettingsValidator.Validate(new HealthSettings
        {
            ProbeIntervalSeconds = interval, FailoverDelaySeconds = failover, FailbackDelaySeconds = failback, SlowLatencyMs = slow,
        }));

    [Theory]
    [InlineData("ftp://example.com/x", "204")]
    [InlineData("not a url", "204")]
    [InlineData("http://example.com/", " ")]
    public void Bad_Probe_Targets_Are_Rejected(string url, string expect) =>
        Assert.NotNull(HealthSettingsValidator.Validate(new HealthSettings { ProbeTargets = [new ProbeTarget { Url = url, Expect = expect }] }));

    [Fact]
    public void No_Probe_Targets_Is_Rejected() =>
        Assert.NotNull(HealthSettingsValidator.Validate(new HealthSettings { ProbeTargets = [] }));
}
