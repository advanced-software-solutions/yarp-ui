namespace YARPUI.Resources;

/// <summary>
/// Marker type for the UI's localized strings. The resx files in this folder are named
/// after this type's full name, so <c>IStringLocalizer&lt;UIStrings&gt;</c> resolves the
/// right resource set (and its satellite assemblies) for the current culture. The same
/// resource feeds the Razor pages, the client-side scripts (serialized into
/// <c>window.YarpUi.strings</c>) and the API/validation error messages.
/// </summary>
public sealed class UIStrings;
