namespace YARPASUI.LoadTests.Support;

/// <summary>
/// Environment-driven knobs for the load harness. The default profile is a short
/// smoke run so a plain <c>dotnet test</c> stays fast; the full run that produces
/// the charts committed under <c>docs/load-tests/</c> is an explicit opt-in:
/// <c>YARP_LOADTEST_PROFILE=full dotnet test YARPASUI.LoadTests/...</c>.
/// </summary>
internal sealed class LoadTestProfile
{
    public static LoadTestProfile Current { get; } = Load();

    public string Name { get; }
    public bool IsFull => Name == "full";

    /// <summary>Concurrent NBomber sessions per level — the cap is 50 by requirement.</summary>
    public IReadOnlyList<int> ConcurrencyLevels { get; }

    public TimeSpan WarmUp { get; }
    public TimeSpan Ramp { get; }
    public TimeSpan Sustain { get; }

    /// <summary>Settle time after the upstream container and the app have started.</summary>
    public TimeSpan AppWarmUp { get; }

    /// <summary><c>container</c> (Testcontainers, default) or <c>local</c> (in-process echo fallback).</summary>
    public string UpstreamMode { get; }

    private LoadTestProfile(
        string name,
        IReadOnlyList<int> levels,
        TimeSpan warmUp,
        TimeSpan ramp,
        TimeSpan sustain,
        TimeSpan appWarmUp,
        string upstreamMode)
    {
        Name = name;
        ConcurrencyLevels = levels;
        WarmUp = warmUp;
        Ramp = ramp;
        Sustain = sustain;
        AppWarmUp = appWarmUp;
        UpstreamMode = upstreamMode;
    }

    private static LoadTestProfile Load()
    {
        var name = Env("YARP_LOADTEST_PROFILE", "smoke");
        var upstream = Env("YARP_LOADTEST_UPSTREAM", "container");

        if (name.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            return new LoadTestProfile(
                "full",
                levels: [10, 25, 50],
                warmUp: TimeSpan.FromSeconds(15),
                ramp: TimeSpan.FromSeconds(10),
                sustain: TimeSpan.FromSeconds(60),
                appWarmUp: TimeSpan.FromSeconds(3),
                upstreamMode: upstream);
        }

        return new LoadTestProfile(
            "smoke",
            levels: [10],
            warmUp: TimeSpan.FromSeconds(5),
            ramp: TimeSpan.FromSeconds(5),
            sustain: TimeSpan.FromSeconds(15),
            appWarmUp: TimeSpan.FromSeconds(1),
            upstreamMode: upstream);
    }

    /// <summary>Estimated wall-clock time of the whole run, for progress messages.</summary>
    public TimeSpan EstimatedDuration =>
        TimeSpan.FromSeconds(ConcurrencyLevels.Count * 3 * (WarmUp + Ramp + Sustain).TotalSeconds);

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
