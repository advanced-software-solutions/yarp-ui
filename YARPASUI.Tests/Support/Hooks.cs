using Reqnroll;
using Reqnroll.UnitTestProvider;

namespace YARPASUI.Tests.Support;

/// <summary>
/// Scenarios tagged @windows rely on Windows-only mechanics (NTFS ACL deny rules via icacls)
/// and cannot run on the Linux CI runner — they are skipped there instead of failing.
/// </summary>
[Binding]
public sealed class PlatformHooks
{
    private readonly ScenarioContext _scenarioContext;

    public PlatformHooks(ScenarioContext scenarioContext)
    {
        _scenarioContext = scenarioContext;
    }

    [BeforeScenario("windows")]
    public void SkipWindowsOnlyScenariosOnOtherPlatforms()
    {
        if (!OperatingSystem.IsWindows())
        {
            var runtimeProvider = _scenarioContext.ScenarioContainer.Resolve<IUnitTestRuntimeProvider>();
            runtimeProvider.TestIgnore("Windows-only scenario (simulates a read-only content root with NTFS ACLs).");
        }
    }
}
