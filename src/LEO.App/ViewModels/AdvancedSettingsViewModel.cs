using CommunityToolkit.Mvvm.ComponentModel;
using Leo.App.Services;
using Leo.Core.Config;
using Leo.Ipc;

namespace Leo.App.ViewModels;

/// <summary>
/// Settings → Advanced: internet-check timing and probe addresses (LEO.md §5.7). Edits stay local until the
/// user presses Save; "Reset to defaults" only fills the fields.
/// </summary>
public sealed partial class AdvancedSettingsViewModel(ServiceConnection connection) : ObservableObject
{
    [ObservableProperty] public partial double IntervalSeconds { get; set; } = 5;
    [ObservableProperty] public partial double FailoverDelaySeconds { get; set; } = 6;
    [ObservableProperty] public partial double FailbackDelaySeconds { get; set; } = 30;
    [ObservableProperty] public partial double SlowLatencyMs { get; set; } = 1000;
    [ObservableProperty] public partial string Probe1Url { get; set; } = "";
    [ObservableProperty] public partial string Probe1Expect { get; set; } = "";
    [ObservableProperty] public partial string Probe2Url { get; set; } = "";
    [ObservableProperty] public partial string Probe2Expect { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool CanEdit => connection.State == ConnectionState.Connected && connection.Access == ClientAccess.Full;

    private bool _autoFailover = true;

    public async Task LoadAsync()
    {
        OnPropertyChanged(nameof(CanEdit));
        var (s, error) = await connection.QueryAsync(c => c.GetHealthSettingsAsync());
        if (s is null)
        {
            ErrorText = error?.Message ?? "Couldn't read the settings.";
            return;
        }
        Fill(s);
        ErrorText = null;
        StatusText = "";
    }

    public async Task SaveAsync()
    {
        if (IsBusy || !CanEdit) return;
        var settings = ToSettings();
        if (HealthSettingsValidator.Validate(settings) is { } problem)
        {
            ErrorText = problem;
            return;
        }

        IsBusy = true;
        UiLog.Write($"Advanced settings: save (every {settings.ProbeIntervalSeconds}s, failover {settings.FailoverDelaySeconds}s, back {settings.FailbackDelaySeconds}s)");
        var err = await connection.TryInvokeAsync(c => c.SetHealthSettingsAsync(settings));
        IsBusy = false;
        if (err is not null)
        {
            ErrorText = err;
            return;
        }
        ErrorText = null;
        StatusText = "Saved. LEO uses the new settings from its next check.";
    }

    public void ResetToDefaults()
    {
        Fill(new HealthSettings { AutoFailover = _autoFailover });
        ErrorText = null;
        StatusText = "Defaults filled in. Press Save to apply them.";
    }

    private void Fill(HealthSettings s)
    {
        _autoFailover = s.AutoFailover;
        IntervalSeconds = s.ProbeIntervalSeconds;
        FailoverDelaySeconds = s.FailoverDelaySeconds;
        FailbackDelaySeconds = s.FailbackDelaySeconds;
        SlowLatencyMs = s.SlowLatencyMs;
        var t1 = s.ProbeTargets.ElementAtOrDefault(0);
        var t2 = s.ProbeTargets.ElementAtOrDefault(1);
        Probe1Url = t1?.Url ?? "";
        Probe1Expect = t1?.Expect ?? "";
        Probe2Url = t2?.Url ?? "";
        Probe2Expect = t2?.Expect ?? "";
    }

    private HealthSettings ToSettings()
    {
        static int I(double v) => double.IsNaN(v) ? 0 : (int)Math.Round(v);
        var targets = new List<ProbeTarget>();
        if (!string.IsNullOrWhiteSpace(Probe1Url)) targets.Add(new ProbeTarget { Url = Probe1Url.Trim(), Expect = Probe1Expect.Trim() });
        if (!string.IsNullOrWhiteSpace(Probe2Url)) targets.Add(new ProbeTarget { Url = Probe2Url.Trim(), Expect = Probe2Expect.Trim() });
        return new HealthSettings
        {
            AutoFailover = _autoFailover,
            ProbeIntervalSeconds = I(IntervalSeconds),
            FailoverDelaySeconds = I(FailoverDelaySeconds),
            FailbackDelaySeconds = I(FailbackDelaySeconds),
            SlowLatencyMs = I(SlowLatencyMs),
            ProbeTargets = targets,
        };
    }
}
