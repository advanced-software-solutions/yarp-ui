using System.Text.Json;

namespace YARPASUI.Tests.Support;

internal static class Json
{
    public static readonly JsonSerializerOptions Insensitive = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Finds a property regardless of casing (the API mixes PascalCase config payloads with camelCase JSON defaults).</summary>
    public static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        Assert.Fail($"Expected JSON property '{name}' in {element.GetRawText()}");
        throw new InvalidOperationException("unreachable");
    }

    public static string? PropertyString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ToString();
            }
        }

        return null;
    }
}
