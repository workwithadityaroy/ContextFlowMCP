using System.Net;
using System.Net.Sockets;

namespace ContextFlow.Infrastructure.Security;

/// <summary>
/// Outbound-request SSRF defenses: allowlisted hosts only, and every resolved address is validated
/// at connect time (defeats DNS rebinding) before the socket is connected to the validated IP.
/// </summary>
public static class SsrfGuard
{
    private const string BlockedMessage = "Outbound request blocked by SSRF policy.";
    private const string ResolveFailedMessage = "Outbound host could not be resolved.";

    /// <summary>Returns true if the address must never be connected to.</summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
        {
            return true; // Unknown shape: fail closed.
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedIPv4(bytes[..written]),
            AddressFamily.InterNetworkV6 => IsBlockedIPv6(bytes[..written]),
            _ => true,
        };
    }

    /// <summary>Returns true if <paramref name="host"/> (case-insensitive) is in the allowlist.</summary>
    public static bool IsHostAllowed(string host, IEnumerable<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedHost = NormalizeHost(host);
        if (IsLocalhostName(normalizedHost))
        {
            return false;
        }

        var hostIsIp = IPAddress.TryParse(normalizedHost, out var hostIp);
        foreach (var allowed in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            var normalizedAllowed = NormalizeHost(allowed);
            if (IsLocalhostName(normalizedAllowed))
            {
                continue;
            }

            if (string.Equals(normalizedAllowed, normalizedHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // IP literals may be spelled differently (e.g. ::ffff:127.0.0.1 vs ::ffff:7f00:1).
            if (hostIsIp && IPAddress.TryParse(normalizedAllowed, out var allowedIp) && hostIp!.Equals(allowedIp))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates a handler that never follows redirects, ignores proxies/cookies, and validates the
    /// resolved IP addresses of every new connection.
    /// </summary>
    /// <param name="allowedHosts">Hosts that may be contacted. Empty rejects everything.</param>
    /// <param name="resolver">Optional DNS resolver (tests inject fakes). Defaults to <see cref="Dns"/>.</param>
    /// <param name="connector">Optional socket stream connector (tests inject fakes). Defaults to TCP <see cref="Socket"/>.</param>
    public static SocketsHttpHandler CreateHandler(
        IEnumerable<string> allowedHosts,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolver = null,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>>? connector = null)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);

        // Snapshot so later mutation of the caller's collection cannot widen the allowlist.
        var allowlist = allowedHosts.ToArray();
        resolver ??= static async (host, ct) => await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        connector ??= ConnectSocketAsync;

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, ct) => ConnectValidatedAsync(context, allowlist, resolver, connector, ct),
        };
    }

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext context,
        string[] allowlist,
        Func<string, CancellationToken, ValueTask<IPAddress[]>> resolver,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connector,
        CancellationToken cancellationToken)
    {
        var endPoint = context.DnsEndPoint;
        var host = NormalizeHost(endPoint.Host);

        if (!IsHostAllowed(host, allowlist))
        {
            throw new HttpRequestException(BlockedMessage);
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                throw new HttpRequestException(ResolveFailedMessage);
            }
        }

        // Reject if ANY address is blocked (do not filter), or nothing resolved.
        if (addresses is null || addresses.Length == 0 || addresses.Any(IsBlockedAddress))
        {
            throw new HttpRequestException(BlockedMessage);
        }

        var target = addresses[0];
        return await connector(new IPEndPoint(target, endPoint.Port), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsLocalhostName(string normalizedHost) =>
        string.Equals(normalizedHost, "localhost", StringComparison.OrdinalIgnoreCase)
        || normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().TrimEnd('.');
        return trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static bool IsBlockedIPv4(ReadOnlySpan<byte> b)
    {
        var first = b[0];
        var second = b[1];

        return first == 0                                          // 0.0.0.0/8 unspecified / "this network"
            || first == 10                                         // 10.0.0.0/8
            || first == 127                                        // loopback
            || (first == 100 && (second & 0xC0) == 64)             // 100.64.0.0/10 CGNAT
            || (first == 169 && second == 254)                     // link-local, cloud metadata
            || (first == 172 && (second & 0xF0) == 16)             // 172.16.0.0/12
            || (first == 192 && second == 168)                     // 192.168.0.0/16
            || (first & 0xF0) == 224                               // 224.0.0.0/4 multicast
            || (first & 0xF0) == 240;                              // 240.0.0.0/4 reserved + broadcast
    }

    private static bool IsBlockedIPv6(ReadOnlySpan<byte> b)
    {
        // ::/96 covers unspecified (::), loopback (::1) and deprecated IPv4-compatible addresses.
        if (b[..12].IndexOfAnyExcept((byte)0) < 0)
        {
            return true;
        }

        // 64:ff9b::/96 NAT64: evaluate the embedded IPv4 address.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].IndexOfAnyExcept((byte)0) < 0)
        {
            return IsBlockedIPv4(b[12..16]);
        }

        // 2002::/16 6to4: evaluate the embedded IPv4 address.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsBlockedIPv4(b[2..6]);
        }

        var first = b[0];
        var second = b[1];

        return (first & 0xFE) == 0xFC                              // fc00::/7 ULA
            || (first == 0xFE && (second & 0xC0) == 0x80)          // fe80::/10 link-local
            || (first == 0xFE && (second & 0xC0) == 0xC0)          // fec0::/10 site-local
            || first == 0xFF;                                      // ff00::/8 multicast
    }
}
