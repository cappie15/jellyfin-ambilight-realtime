using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.RealtimeAmbilight.Core.Discovery;

/// <summary>
/// One service instance read from an mDNS response: the instance label, the host
/// it points at, its port and, when the responder included an A record in the
/// same message, its IPv4 address.
/// </summary>
public sealed record MdnsServiceInstance(string InstanceName, string HostName, int Port, string? IPv4Address);

/// <summary>
/// Minimal mDNS (RFC 6762) query builder and response reader covering only the
/// record types needed to locate a service: PTR, SRV and A.
/// </summary>
/// <remarks>
/// WLED answers mDNS but does not answer SSDP unless its Alexa emulation is
/// enabled, so mDNS is the only discovery transport that finds a stock
/// controller. Malformed or truncated messages yield no instances rather than
/// throwing: discovery reads whatever untrusted devices put on the wire.
/// </remarks>
public static class MdnsMessage
{
    /// <summary>The DNS-SD service type WLED advertises.</summary>
    public const string WledServiceName = "_wled._tcp.local";

    private const int HeaderLength = 12;
    private const int TypeA = 1;
    private const int TypePtr = 12;
    private const int TypeSrv = 33;
    private const int ClassInternet = 1;
    private const int ResponseFlag = 0x8000;
    private const int CompressionMask = 0xC0;
    private const int MaxCompressionJumps = 64;
    private const int MaxLabelLength = 63;

    /// <summary>Builds a PTR query for <paramref name="serviceName"/>.</summary>
    public static byte[] CreateQuery(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var labels = serviceName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var nameLength = 1;
        foreach (var label in labels)
        {
            var labelLength = Encoding.UTF8.GetByteCount(label);
            if (labelLength > MaxLabelLength)
            {
                throw new ArgumentException($"Label '{label}' exceeds {MaxLabelLength} bytes.", nameof(serviceName));
            }

            nameLength += 1 + labelLength;
        }

        var query = new byte[HeaderLength + nameLength + 4];
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);

        var offset = HeaderLength;
        foreach (var label in labels)
        {
            var written = Encoding.UTF8.GetBytes(label, query.AsSpan(offset + 1));
            query[offset] = (byte)written;
            offset += 1 + written;
        }

        query[offset] = 0;
        offset++;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset, 2), TypePtr);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset + 2, 2), ClassInternet);
        return query;
    }

    /// <summary>
    /// Reads every instance of <paramref name="serviceName"/> announced in
    /// <paramref name="message"/>. Returns an empty list for anything that is not
    /// a parseable response.
    /// </summary>
    public static IReadOnlyList<MdnsServiceInstance> ParseResponse(ReadOnlySpan<byte> message, string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (message.Length < HeaderLength
            || (BinaryPrimitives.ReadUInt16BigEndian(message.Slice(2, 2)) & ResponseFlag) == 0)
        {
            return [];
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(4, 2));
        var recordCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(6, 2))
            + BinaryPrimitives.ReadUInt16BigEndian(message.Slice(8, 2))
            + BinaryPrimitives.ReadUInt16BigEndian(message.Slice(10, 2));

        var offset = HeaderLength;
        for (var question = 0; question < questionCount; question++)
        {
            if (!TryReadName(message, offset, out _, out offset))
            {
                return [];
            }

            offset += 4;
        }

        var suffix = "." + serviceName;
        var instances = new List<(string Instance, string Host, int Port)>();
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var record = 0; record < recordCount; record++)
        {
            if (!TryReadName(message, offset, out var recordName, out offset) || offset + 10 > message.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset + 8, 2));
            offset += 10;
            if (offset + dataLength > message.Length)
            {
                break;
            }

            if (type == TypeSrv
                && dataLength >= 7
                && recordName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset + 4, 2));
                if (port > 0 && TryReadName(message, offset + 6, out var target, out _) && target.Length > 0)
                {
                    instances.Add((recordName[..^suffix.Length], target, port));
                }
            }
            else if (type == TypeA && dataLength == 4)
            {
                addresses[recordName] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{message[offset]}.{message[offset + 1]}.{message[offset + 2]}.{message[offset + 3]}");
            }

            offset += dataLength;
        }

        var results = new MdnsServiceInstance[instances.Count];
        for (var index = 0; index < instances.Count; index++)
        {
            var (instance, host, port) = instances[index];
            results[index] = new MdnsServiceInstance(
                instance,
                host,
                port,
                addresses.TryGetValue(host, out var address) ? address : null);
        }

        return results;
    }

    /// <summary>
    /// Reads a possibly compressed DNS name. <paramref name="nextOffset"/> is the
    /// offset just past the name as it appears at <paramref name="offset"/>, so a
    /// compression pointer consumes two bytes regardless of what it expands to.
    /// </summary>
    private static bool TryReadName(ReadOnlySpan<byte> message, int offset, out string name, out int nextOffset)
    {
        name = string.Empty;
        nextOffset = offset;

        var builder = new StringBuilder();
        var jumps = 0;
        var afterName = -1;

        while (true)
        {
            if (offset < 0 || offset >= message.Length)
            {
                return false;
            }

            var length = message[offset];
            if (length == 0)
            {
                offset++;
                break;
            }

            if ((length & CompressionMask) == CompressionMask)
            {
                if (offset + 1 >= message.Length || ++jumps > MaxCompressionJumps)
                {
                    return false;
                }

                if (afterName < 0)
                {
                    afterName = offset + 2;
                }

                offset = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2)) & 0x3FFF;
                continue;
            }

            if ((length & CompressionMask) != 0 || offset + 1 + length > message.Length)
            {
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.UTF8.GetString(message.Slice(offset + 1, length)));
            offset += 1 + length;
        }

        name = builder.ToString();
        nextOffset = afterName >= 0 ? afterName : offset;
        return true;
    }
}
