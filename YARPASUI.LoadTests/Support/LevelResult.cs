namespace YARPASUI.LoadTests.Support;

/// <summary>Measured results of one scenario at one concurrency level.</summary>
internal sealed record LevelResult(
    string Scenario,
    int Copies,
    double Rps,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    long OkCount,
    long FailCount,
    double DurationSec);
