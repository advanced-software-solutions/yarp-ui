using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace YARPUI.Services.IpBlocking;

/// <summary>The kind of address range a blocking rule covers.</summary>
public enum IpBlockRuleKind
{
    Single,
    Cidr,
    Range,
}

/// <summary>
/// One entry in the IP block list: an address (single), a CIDR network (203.0.113.0/24)
/// or an inclusive from–to range (203.0.113.5-203.0.113.99). The value is always stored in
/// canonical form (network address for CIDR, normalized address order for ranges).
/// </summary>
public sealed class IpBlockRule
{
    public string Id { get; init; } = "";
    public string Value { get; init; } = "";
    public IpBlockRuleKind Kind { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>A rule value compiled into its matchable form: an inclusive min/max pair per family.</summary>
/// <param name="IsIpv4">
/// IPv4 rules carry their bounds in <see cref="MinLo"/>/<see cref="MaxLo"/> (MinHi/MaxHi are 0);
/// IPv6 rules use the full 128-bit big-endian halves.</param>
internal sealed record IpBlockRuleCompiled(
    bool IsIpv4,
    ulong MinHi,
    ulong MinLo,
    ulong MaxHi,
    ulong MaxLo);

internal enum IpBlockParseReason
{
    NotAnAddress,
    HostBitsSet,
    RangeOutOfOrder,
    RangeFamilyMismatch,
}

internal sealed record IpBlockParseFailure(IpBlockParseReason Reason, string? Suggestion = null);

internal sealed record IpBlockRuleParsed(IpBlockRuleKind Kind, string CanonicalValue, IpBlockRuleCompiled Compiled);

/// <summary>
/// Parses block-rule values. Runs only when rules are loaded or edited — never on the request
/// path — so it favors clarity over allocation frugality. IPv4-mapped IPv6 input
/// (::ffff:203.0.113.7) is normalized to IPv4; everything is reduced to big-endian bounds.
/// </summary>
internal static class IpBlockRuleParser
{
    public static bool TryParse(string? input, out IpBlockRuleParsed? parsed, out IpBlockParseFailure? failure)
    {
        parsed = null;
        var value = (input ?? "").Trim();
        if (value.Length == 0)
        {
            failure = new IpBlockParseFailure(IpBlockParseReason.NotAnAddress);
            return false;
        }

        // '-' never appears inside an IP literal, so its presence means a from–to range.
        if (value.Contains('-'))
        {
            return TryParseRange(value, out parsed, out failure);
        }

        if (value.Contains('/'))
        {
            return TryParseCidr(value, out parsed, out failure);
        }

        if (!TryParseAddress(value, out var address))
        {
            failure = new IpBlockParseFailure(IpBlockParseReason.NotAnAddress);
            return false;
        }

        var compiled = new IpBlockRuleCompiled(address.IsIpv4, address.Hi, address.Lo, address.Hi, address.Lo);
        parsed = new IpBlockRuleParsed(IpBlockRuleKind.Single, address.CanonicalText, compiled);
        failure = null;
        return true;
    }

    /// <summary>Parses a plain address (no CIDR/range syntax) and normalizes mapped IPv6.</summary>
    public static bool TryParseAddress(string? input, out (bool IsIpv4, ulong Hi, ulong Lo, string CanonicalText) address)
    {
        address = default;
        var value = (input ?? "").Trim();
        if (value.Length == 0 || !IPAddress.TryParse(value, out var parsed))
        {
            return false;
        }

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!parsed.TryWriteBytes(bytes, out var written))
        {
            return false;
        }

        if (written == 4)
        {
            var v4 = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            address = (true, 0, v4, Format(v4));
            return true;
        }

        if (written == 16)
        {
            var hi = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            var lo = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
            address = (false, hi, lo, Format(hi, lo));
            return true;
        }

        return false;
    }

    private static bool TryParseRange(string value, out IpBlockRuleParsed? parsed, out IpBlockParseFailure? failure)
    {
        parsed = null;
        var parts = value.Split('-');
        if (parts.Length != 2 ||
            !TryParseAddress(parts[0], out var from) ||
            !TryParseAddress(parts[1], out var to))
        {
            failure = new IpBlockParseFailure(IpBlockParseReason.NotAnAddress);
            return false;
        }

        if (from.IsIpv4 != to.IsIpv4)
        {
            failure = new IpBlockParseFailure(IpBlockParseReason.RangeFamilyMismatch);
            return false;
        }

        if (Compare(from.Hi, from.Lo, to.Hi, to.Lo) > 0)
        {
            failure = new IpBlockParseFailure(IpBlockParseReason.RangeOutOfOrder);
            return false;
        }

        var compiled = new IpBlockRuleCompiled(from.IsIpv4, from.Hi, from.Lo, to.Hi, to.Lo);
        parsed = new IpBlockRuleParsed(IpBlockRuleKind.Range, $"{from.CanonicalText}-{to.CanonicalText}", compiled);
        failure = null;
        return true;
    }

    private static bool TryParseCidr(string value, out IpBlockRuleParsed? parsed, out IpBlockParseFailure? failure)
    {
        parsed = null;
        var parts = value.Split('/');
        if (parts.Length != 2 ||
            !TryParseAddress(parts[0], out var baseAddress) ||
            !int.TryParse(parts[1], out var prefixLength) ||
            prefixLength < 0 ||
            (baseAddress.IsIpv4 && prefixLength > 32) ||
            (!baseAddress.IsIpv4 && prefixLength > 128))
        {
            failure = new IpBlockParseFailure(IpBlockParseReason.NotAnAddress);
            return false;
        }

        ulong minHi, minLo, maxHi, maxLo;
        if (baseAddress.IsIpv4)
        {
            var mask = prefixLength == 0 ? 0UL : ulong.MaxValue << (32 - prefixLength);
            var network = baseAddress.Lo & mask;
            if (network != baseAddress.Lo)
            {
                failure = new IpBlockParseFailure(
                    IpBlockParseReason.HostBitsSet,
                    Suggestion: $"{Format((uint)network)}/{prefixLength}");
                return false;
            }

            (minHi, minLo, maxHi, maxLo) = (0UL, network, 0UL, network | ~mask & 0xFFFFFFFFUL);
        }
        else if (prefixLength == 0)
        {
            (minHi, minLo, maxHi, maxLo) = (0UL, 0UL, ulong.MaxValue, ulong.MaxValue);
        }
        else if (prefixLength <= 64)
        {
            var maskHi = ulong.MaxValue << (64 - prefixLength);
            minHi = baseAddress.Hi & maskHi;
            (minLo, maxHi, maxLo) = (0UL, minHi | ~maskHi, ulong.MaxValue);
        }
        else
        {
            var maskLo = ulong.MaxValue << (128 - prefixLength);
            minHi = baseAddress.Hi;
            minLo = baseAddress.Lo & maskLo;
            (maxHi, maxLo) = (baseAddress.Hi, minLo | ~maskLo);
        }

        var compiled = new IpBlockRuleCompiled(baseAddress.IsIpv4, minHi, minLo, maxHi, maxLo);
        var canonical = baseAddress.IsIpv4
            ? $"{Format((uint)minLo)}/{prefixLength}"
            : $"{Format(minHi, minLo)}/{prefixLength}";
        parsed = new IpBlockRuleParsed(IpBlockRuleKind.Cidr, canonical, compiled);
        failure = null;
        return true;
    }

    /// <summary>Big-endian tuple compare: negative when a &lt; b, positive when a &gt; b.</summary>
    public static int Compare(ulong aHi, ulong aLo, ulong bHi, ulong bLo) =>
        aHi != bHi ? aHi.CompareTo(bHi) : aLo.CompareTo(bLo);

    // Rebuilding the text from the parsed bytes (instead of echoing the input) canonicalizes
    // mapped-IPv6 forms, uppercase hex and stray whitespace.
    private static string Format(ulong hi, ulong lo)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, hi);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], lo);
        return new IPAddress(bytes).ToString();
    }

    private static string Format(uint v4)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, v4);
        return new IPAddress(bytes).ToString();
    }
}
