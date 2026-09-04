using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Localization;
using YARPUI.Resources;

namespace YARPUI.Services.IpBlocking;

public sealed record IpBlockRuleResult(bool Success, IpBlockRule? Rule, IReadOnlyList<string> Errors);

/// <summary>Outcome of the page's "test an address" check: which rule (if any) an address would hit.</summary>
public sealed record IpBlockCheckResult(bool Valid, bool Blocked, IpBlockRule? Rule, string? Error);

/// <summary>
/// Owns the IP block list: UI-owned state persisted as yarp-ui-ipblocklist.json in the data
/// directory (atomic tmp+move writes, like yarp-ui.routes.json). Edits rebuild the immutable
/// <see cref="IpBlockListMatcher"/> and swap the current snapshot reference, so the request
/// path never locks — it reads the snapshot once per request. A corrupt or hand-edited file
/// never takes the app down: unreadable content falls back to an empty list and individual
/// rules that no longer parse are skipped with a warning.
/// </summary>
public sealed class IpBlockListService
{
    public const string BlockListFileName = "yarp-ui-ipblocklist.json";
    public const int MaxRules = 1000;

    private static readonly JsonDocumentOptions LenientDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly ILogger<IpBlockListService> _logger;
    private readonly IStringLocalizer<UIStrings> _localizer;
    private readonly object _sync = new();
    private volatile Snapshot _current;

    public IpBlockListService(string dataDirectory, ILogger<IpBlockListService> logger, IStringLocalizer<UIStrings> localizer)
    {
        _filePath = Path.Combine(dataDirectory, BlockListFileName);
        _logger = logger;
        // The ResourceManager localizer resolves the culture per call, so sharing one instance
        // across requests (this service is a singleton) stays culture-correct.
        _localizer = localizer;
        _current = Load() ?? Snapshot.Empty;
    }

    private string L(string key, params object[] args) => _localizer[key, args];

    public string BlockListPath => _filePath;

    /// <summary>The current rules, oldest first (immutable snapshot; cheap to read without locks).</summary>
    public IReadOnlyList<IpBlockRule> Rules => _current.Rules;

    public bool TrustForwardedFor => _current.TrustForwardedFor;

    /// <summary>The compiled snapshot the blocking middleware matches against.</summary>
    public IpBlockListMatcher CurrentMatcher => _current.Matcher;

    /// <summary>
    /// The address blocking decisions are made against: the direct TCP peer, or — when the
    /// X-Forwarded-For toggle is on — the leftmost forwarded entry when a fronting proxy
    /// supplied one. The header is caller-controlled; enable it only behind a trusted proxy
    /// (see the README). IPv4-mapped IPv6 forms are normalized to IPv4. Null when nothing is
    /// known — requests without a resolvable address are never blocked.
    /// </summary>
    public System.Net.IPAddress? ResolveClientIp(HttpContext context)
    {
        if (_current.TrustForwardedFor)
        {
            var forwarded = RequestClientIp.Resolve(context);
            if (!string.IsNullOrEmpty(forwarded) && System.Net.IPAddress.TryParse(forwarded, out var parsed))
            {
                return Normalize(parsed);
            }
        }

        return Normalize(context.Connection.RemoteIpAddress);
    }

    /// <summary>Which rule a plain address would hit — powers the page's "test an address" box.</summary>
    public IpBlockCheckResult CheckAddress(string? address)
    {
        var value = (address ?? "").Trim();
        if (value.Length == 0 || !System.Net.IPAddress.TryParse(value, out var parsed))
        {
            return new IpBlockCheckResult(false, false, null, L("validation.ipInvalid", value));
        }

        return _current.Matcher.IsBlocked(parsed, out var rule)
            ? new IpBlockCheckResult(true, true, rule, null)
            : new IpBlockCheckResult(true, false, null, null);
    }

    public IpBlockRuleResult AddRule(string? value, string? note)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new IpBlockRuleResult(false, null, [L("validation.ipRequired")]);
        }

        lock (_sync)
        {
            if (!IpBlockRuleParser.TryParse(value, out var parsed, out var failure) || parsed is null)
            {
                return new IpBlockRuleResult(false, null, [LocalizedParseError(value!, failure)]);
            }

            if (_current.Rules.Any(r => string.Equals(r.Value, parsed.CanonicalValue, StringComparison.OrdinalIgnoreCase)))
            {
                return new IpBlockRuleResult(false, null, [L("validation.ipDuplicate", parsed.CanonicalValue)]);
            }

            if (_current.Rules.Count >= MaxRules)
            {
                return new IpBlockRuleResult(false, null, [L("validation.ipRuleLimit", MaxRules)]);
            }

            var rule = new IpBlockRule
            {
                Id = NewId(),
                Value = parsed.CanonicalValue,
                Kind = parsed.Kind,
                Note = string.IsNullOrWhiteSpace(note) ? null : note!.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
            };
            _current = Publish(_current.Rules.Append(rule).ToList(), _current.TrustForwardedFor);
            _logger.LogInformation("IP block rule added: {Rule}", rule.Value);
            return new IpBlockRuleResult(true, rule, Array.Empty<string>());
        }
    }

    public bool RemoveRule(string id)
    {
        lock (_sync)
        {
            var rules = _current.Rules;
            var rule = rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                return false;
            }

            _current = Publish(rules.Where(r => !ReferenceEquals(r, rule)).ToList(), _current.TrustForwardedFor);
            _logger.LogInformation("IP block rule removed: {Rule}", rule.Value);
            return true;
        }
    }

    public void SetTrustForwardedFor(bool value)
    {
        lock (_sync)
        {
            _current = Publish(_current.Rules.ToList(), value);
            _logger.LogInformation("IP blocking forwarded-header trust set to {Value}.", value);
        }
    }

    private string LocalizedParseError(string value, IpBlockParseFailure? failure)
    {
        return failure?.Reason switch
        {
            IpBlockParseReason.HostBitsSet => L("validation.ipHostBits", value.Trim(), failure.Suggestion ?? ""),
            IpBlockParseReason.RangeOutOfOrder => L("validation.ipRangeOrder", value.Trim()),
            IpBlockParseReason.RangeFamilyMismatch => L("validation.ipRangeFamily", value.Trim()),
            _ => L("validation.ipInvalid", value.Trim()),
        };
    }

    private Snapshot Publish(List<IpBlockRule> rules, bool trustForwardedFor)
    {
        Persist(rules, trustForwardedFor);
        return new Snapshot
        {
            Rules = rules,
            TrustForwardedFor = trustForwardedFor,
            Matcher = new IpBlockListMatcher(rules),
        };
    }

    private static System.Net.IPAddress? Normalize(System.Net.IPAddress? address) =>
        address is null ? null : address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // Short but collision-checked: uniqueness only has to hold within the current list.
    private string NewId()
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (_current.Rules.All(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return id;
            }
        }
    }

    // ---- persistence ----

    private Snapshot? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var root = JsonNode.Parse(File.ReadAllText(_filePath), documentOptions: LenientDocumentOptions) as JsonObject;
            if (root is null)
            {
                _logger.LogWarning("The IP block list file {Path} is not a JSON object; starting with an empty list.", _filePath);
                return null;
            }

            var trust = root["settings"]?["trustForwardedFor"] is JsonValue trustValue
                && trustValue.TryGetValue<bool>(out var trustParsed) && trustParsed;

            var rules = new List<IpBlockRule>();
            if (root["rules"] is JsonArray ruleNodes)
            {
                foreach (var node in ruleNodes)
                {
                    if (node is not JsonObject ruleNode)
                    {
                        continue;
                    }

                    var id = ReadString(ruleNode, "id");
                    var value = ReadString(ruleNode, "value");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value))
                    {
                        _logger.LogWarning("Skipping an IP block rule without id or value in {Path}.", _filePath);
                        continue;
                    }

                    if (!IpBlockRuleParser.TryParse(value, out var parsed, out _) || parsed is null)
                    {
                        _logger.LogWarning("Skipping the IP block rule '{Value}' in {Path}: it does not parse.", value, _filePath);
                        continue;
                    }

                    if (rules.Any(r => string.Equals(r.Value, parsed.CanonicalValue, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue; // duplicate in the file — first one wins
                    }

                    var created = ReadDateTime(ruleNode, "createdAtUtc") ?? DateTime.UtcNow;
                    rules.Add(new IpBlockRule
                    {
                        Id = id,
                        Value = parsed.CanonicalValue,
                        Kind = parsed.Kind,
                        Note = ReadString(ruleNode, "note"),
                        CreatedAtUtc = created,
                    });
                }
            }

            return new Snapshot
            {
                Rules = rules,
                TrustForwardedFor = trust,
                Matcher = new IpBlockListMatcher(rules),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the IP block list file {Path}; starting with an empty list.", _filePath);
            return null;
        }
    }

    private void Persist(IReadOnlyList<IpBlockRule> rules, bool trustForwardedFor)
    {
        var root = new JsonObject
        {
            ["version"] = 1,
            ["settings"] = new JsonObject { ["trustForwardedFor"] = trustForwardedFor },
            ["rules"] = new JsonArray(rules.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id,
                ["value"] = r.Value,
                ["note"] = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note,
                ["createdAtUtc"] = r.CreatedAtUtc,
            }).ToArray()),
        };

        // Rename-replace is safe here (nothing watches this file); a torn write can never
        // replace the previous list. Same pattern as ProxyConfigService.Persist.
        var tmpPath = _filePath + ".tmp";
        File.WriteAllText(tmpPath, root.ToJsonString(SerializerOptions));
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private static string? ReadString(JsonObject node, string name)
    {
        if (node[name] is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DateTime? ReadDateTime(JsonObject node, string name)
    {
        if (node[name] is JsonValue value && value.TryGetValue<DateTime>(out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    /// <summary>Everything the request path and the API read; replaced wholesale on change.</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new()
        {
            Rules = Array.Empty<IpBlockRule>(),
            TrustForwardedFor = false,
            Matcher = IpBlockListMatcher.Empty,
        };

        public required IReadOnlyList<IpBlockRule> Rules { get; init; }
        public required bool TrustForwardedFor { get; init; }
        public required IpBlockListMatcher Matcher { get; init; }
    }
}
