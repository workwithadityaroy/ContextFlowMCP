using System.Net;
using ContextFlow.Infrastructure.Security;

namespace ContextFlow.UnitTests;

public sealed class SsrfGuardTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::7f00:1")]
    public void IsBlockedAddressReturnsTrueForInternalAddresses(string address)
    {
        Assert.True(SsrfGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsBlockedAddressReturnsFalseForPublicAddresses(string address)
    {
        Assert.False(SsrfGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("API.EXAMPLE.COM", true)]
    [InlineData("other.example.com", false)]
    [InlineData("", false)]
    public void IsHostAllowedMatchesCaseInsensitively(string host, bool expected)
    {
        Assert.Equal(expected, SsrfGuard.IsHostAllowed(host, ["api.example.com"]));
    }

    [Fact]
    public void IsHostAllowedRejectsEverythingWhenAllowlistEmpty()
    {
        Assert.False(SsrfGuard.IsHostAllowed("api.example.com", []));
    }

    [Fact]
    public void HandlerDisablesRedirectsCookiesAndProxy()
    {
        using var handler = SsrfGuard.CreateHandler(["api.example.com"]);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("http://127.0.0.1/", "127.0.0.1")]
    [InlineData("http://169.254.169.254/", "169.254.169.254")]
    [InlineData("http://[::1]/", "[::1]")]
    [InlineData("http://[::ffff:127.0.0.1]/", "::ffff:127.0.0.1")]
    [InlineData("http://10.0.0.1/", "10.0.0.1")]
    public async Task AllowlistedPrivateIpLiteralIsBlockedByIpCheck(string url, string allowedHost)
    {
        var resolverCalls = 0;
        using var client = CreateClient([allowedHost], (_, _) =>
        {
            resolverCalls++;
            return ValueTask.FromResult<IPAddress[]>([]);
        });

        using var cts = new CancellationTokenSource(TestTimeout);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url, cts.Token));
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task AllowlistedLocalhostResolvingToLoopbackIsBlocked()
    {
        using var client = CreateClient(["localhost"], (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Loopback, IPAddress.IPv6Loopback]));

        using var cts = new CancellationTokenSource(TestTimeout);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost/", cts.Token));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://example.com/")]
    public async Task NonAllowlistedHostIsRejectedWithoutResolving(string url)
    {
        var resolverCalls = 0;
        using var client = CreateClient(["api.example.com"], (_, _) =>
        {
            resolverCalls++;
            return ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8")]);
        });

        using var cts = new CancellationTokenSource(TestTimeout);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url, cts.Token));
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task EmptyAllowlistRejectsEverything()
    {
        using var client = CreateClient([], (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8")]));

        using var cts = new CancellationTokenSource(TestTimeout);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://api.example.com/", cts.Token));
    }

    [Fact]
    public async Task AllowlistedHostRebindingToPrivateIpIsBlocked()
    {
        using var client = CreateClient(["api.example.com"], (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("10.0.0.5")]));

        using var cts = new CancellationTokenSource(TestTimeout);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://api.example.com/", cts.Token));
        Assert.Contains("SSRF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixedPublicAndPrivateResolutionIsBlocked()
    {
        using var client = CreateClient(["api.example.com"], (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("169.254.169.254")]));

        using var cts = new CancellationTokenSource(TestTimeout);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://api.example.com/", cts.Token));
    }

    [Fact]
    public async Task EmptyResolutionIsBlocked()
    {
        using var client = CreateClient(["api.example.com"], (_, _) => ValueTask.FromResult<IPAddress[]>([]));

        using var cts = new CancellationTokenSource(TestTimeout);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://api.example.com/", cts.Token));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("sub.localhost")]
    public void IsHostAllowedNeverAllowsLocalhostEvenWhenInAllowlist(string host)
    {
        Assert.False(SsrfGuard.IsHostAllowed(host, ["localhost", "sub.localhost", "api.example.com"]));
    }

    [Fact]
    public async Task GuardedClientDoesNotFollowRedirectToMetadataEndpoint()
    {
        var connectedEndpoints = new List<IPEndPoint>();
        var resolvedHosts = new List<string>();
        const string rawRedirectResponse =
            "HTTP/1.1 302 Found\r\n" +
            "Location: http://169.254.169.254/latest/meta-data/iam/security-credentials/\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";

        using var handler = SsrfGuard.CreateHandler(
            ["api.example.com"],
            resolver: (host, _) =>
            {
                resolvedHosts.Add(host);
                return ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]);
            },
            connector: (endPoint, _) =>
            {
                connectedEndpoints.Add(endPoint);
                return ValueTask.FromResult<Stream>(new CannedHttpStream(rawRedirectResponse));
            });

        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TestTimeout };
        using var cts = new CancellationTokenSource(TestTimeout);

        using var response = await client.GetAsync("http://api.example.com/v1/latest?base=USD", cts.Token);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(connectedEndpoints);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 80), connectedEndpoints[0]);
        Assert.Equal(["api.example.com"], resolvedHosts);
    }

    private static HttpClient CreateClient(string[] allowedHosts, Func<string, CancellationToken, ValueTask<IPAddress[]>> resolver) =>
        new(SsrfGuard.CreateHandler(allowedHosts, resolver), disposeHandler: true) { Timeout = TestTimeout };

    private sealed class CannedHttpStream(string rawHttpResponse) : Stream
    {
        private readonly MemoryStream _response = new(System.Text.Encoding.ASCII.GetBytes(rawHttpResponse));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _response.Length;
        public override long Position
        {
            get => _response.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _response.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _response.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}

