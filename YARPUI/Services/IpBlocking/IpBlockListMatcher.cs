using System.Buffers.Binary;
using System.Net;

namespace YARPUI.Services.IpBlocking;

/// <summary>
/// An immutable, precompiled snapshot of the block list. Everything expensive (parsing,
/// CIDR math, sorting) happens at construction — when a rule changes the service builds a
/// new snapshot and swaps the reference, so requests never take locks or allocate:
/// IPv4 singles are one dictionary lookup, IPv4 networks a binary search over sorted,
/// merged intervals; IPv6 uses the same scheme on 128-bit big-endian halves.
/// </summary>
public sealed class IpBlockListMatcher
{
    public static IpBlockListMatcher Empty { get; } = new(Array.Empty<IpBlockRule>());

    private readonly Dictionary<uint, IpBlockRule> _v4Singles;
    private readonly (uint Min, uint Max, IpBlockRule Rule)[] _v4Ranges;
    private readonly Dictionary<(ulong Hi, ulong Lo), IpBlockRule> _v6Singles;
    private readonly (ulong MinHi, ulong MinLo, ulong MaxHi, ulong MaxLo, IpBlockRule Rule)[] _v6Ranges;

    public bool IsEmpty { get; }

    public IpBlockListMatcher(IEnumerable<IpBlockRule> rules)
    {
        var v4Singles = new Dictionary<uint, IpBlockRule>();
        var v6Singles = new Dictionary<(ulong Hi, ulong Lo), IpBlockRule>();
        var v4Ranges = new List<(uint Min, uint Max, IpBlockRule Rule)>();
        var v6Ranges = new List<(ulong MinHi, ulong MinLo, ulong MaxHi, ulong MaxLo, IpBlockRule Rule)>();

        // Rules are validated before they reach the matcher; anything that no longer parses
        // (hand-edited file) is skipped by both the service and here.
        foreach (var rule in rules)
        {
            if (!IpBlockRuleParser.TryParse(rule.Value, out var parsed, out _) || parsed is null)
            {
                continue;
            }

            var compiled = parsed.Compiled;
            if (compiled.IsIpv4)
            {
                var min = (uint)compiled.MinLo;
                var max = (uint)compiled.MaxLo;
                if (min == max)
                {
                    v4Singles[min] = rule;
                }
                else
                {
                    v4Ranges.Add((min, max, rule));
                }
            }
            else if (compiled.MinHi == compiled.MaxHi && compiled.MinLo == compiled.MaxLo)
            {
                v6Singles[(compiled.MinHi, compiled.MinLo)] = rule;
            }
            else
            {
                v6Ranges.Add((compiled.MinHi, compiled.MinLo, compiled.MaxHi, compiled.MaxLo, rule));
            }
        }

        _v4Singles = v4Singles;
        _v6Singles = v6Singles;
        _v4Ranges = MergeIntervals(v4Ranges);
        _v6Ranges = MergeIntervals(v6Ranges);
        IsEmpty = v4Singles.Count == 0 && v6Singles.Count == 0 && _v4Ranges.Length == 0 && _v6Ranges.Length == 0;
    }

    /// <summary>
    /// Whether <paramref name="address"/> is blocked; <paramref name="rule"/> receives the
    /// matching rule (null when allowed). IPv4-mapped IPv6 addresses are treated as IPv4.
    /// Allocation-free on the hot path.
    /// </summary>
    public bool IsBlocked(IPAddress address, out IpBlockRule? rule)
    {
        rule = null;
        if (IsEmpty)
        {
            return false;
        }

        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        Span<byte> bytes = stackalloc byte[16];
        if (!ip.TryWriteBytes(bytes, out var written))
        {
            return false;
        }

        return written == 4
            ? TryMatchV4(BinaryPrimitives.ReadUInt32BigEndian(bytes), out rule)
            : TryMatchV6(
                BinaryPrimitives.ReadUInt64BigEndian(bytes),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
                out rule);
    }

    private bool TryMatchV4(uint value, out IpBlockRule? rule)
    {
        if (_v4Singles.TryGetValue(value, out rule!))
        {
            return true;
        }

        var ranges = _v4Ranges;
        var index = -1;
        int low = 0, high = ranges.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) >> 1;
            if (ranges[mid].Min <= value)
            {
                index = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (index >= 0 && value <= ranges[index].Max)
        {
            rule = ranges[index].Rule;
            return true;
        }

        rule = null;
        return false;
    }

    private bool TryMatchV6(ulong hi, ulong lo, out IpBlockRule? rule)
    {
        if (_v6Singles.TryGetValue((hi, lo), out rule!))
        {
            return true;
        }

        var ranges = _v6Ranges;
        var index = -1;
        int low = 0, high = ranges.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) >> 1;
            if (IpBlockRuleParser.Compare(ranges[mid].MinHi, ranges[mid].MinLo, hi, lo) <= 0)
            {
                index = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (index >= 0 && IpBlockRuleParser.Compare(hi, lo, ranges[index].MaxHi, ranges[index].MaxLo) <= 0)
        {
            rule = ranges[index].Rule;
            return true;
        }

        rule = null;
        return false;
    }

    // Overlapping intervals are merged into one, keeping the rule that starts lowest. The
    // match outcome is identical (any overlap blocks); only which rule the log names changes.
    private static (uint Min, uint Max, IpBlockRule Rule)[] MergeIntervals(List<(uint Min, uint Max, IpBlockRule Rule)> ranges)
    {
        if (ranges.Count == 0)
        {
            return Array.Empty<(uint, uint, IpBlockRule)>();
        }

        ranges.Sort((a, b) => a.Min.CompareTo(b.Min));
        var merged = new List<(uint Min, uint Max, IpBlockRule Rule)>(ranges.Count);
        var (min, max, rule) = ranges[0];
        for (var i = 1; i < ranges.Count; i++)
        {
            var next = ranges[i];
            if (next.Min <= max)
            {
                if (next.Max > max)
                {
                    max = next.Max;
                }
            }
            else
            {
                merged.Add((min, max, rule));
                (min, max, rule) = next;
            }
        }

        merged.Add((min, max, rule));
        return merged.ToArray();
    }

    private static (ulong MinHi, ulong MinLo, ulong MaxHi, ulong MaxLo, IpBlockRule Rule)[] MergeIntervals(
        List<(ulong MinHi, ulong MinLo, ulong MaxHi, ulong MaxLo, IpBlockRule Rule)> ranges)
    {
        if (ranges.Count == 0)
        {
            return Array.Empty<(ulong, ulong, ulong, ulong, IpBlockRule)>();
        }

        ranges.Sort((a, b) => IpBlockRuleParser.Compare(a.MinHi, a.MinLo, b.MinHi, b.MinLo));
        var merged = new List<(ulong MinHi, ulong MinLo, ulong MaxHi, ulong MaxLo, IpBlockRule Rule)>(ranges.Count);
        var (minHi, minLo, maxHi, maxLo, rule) = ranges[0];
        for (var i = 1; i < ranges.Count; i++)
        {
            var next = ranges[i];
            if (IpBlockRuleParser.Compare(next.MinHi, next.MinLo, maxHi, maxLo) <= 0)
            {
                if (IpBlockRuleParser.Compare(next.MaxHi, next.MaxLo, maxHi, maxLo) > 0)
                {
                    (maxHi, maxLo) = (next.MaxHi, next.MaxLo);
                }
            }
            else
            {
                merged.Add((minHi, minLo, maxHi, maxLo, rule));
                (minHi, minLo, maxHi, maxLo, rule) = next;
            }
        }

        merged.Add((minHi, minLo, maxHi, maxLo, rule));
        return merged.ToArray();
    }
}
