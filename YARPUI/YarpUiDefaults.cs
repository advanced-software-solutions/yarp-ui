namespace YARPUI;

/// <summary>
/// Well-known names for the UI's authentication scheme and authorization policy.
/// The UI always authenticates with its own cookie scheme (and never changes the host
/// application's default scheme), so it can be added to apps with existing auth.
/// </summary>
public static class YarpUiDefaults
{
    public const string Scheme = "YarpUi.Auth";
    public const string Policy = "YarpUi";
}
