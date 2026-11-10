using System.Net;
using SecurityToolkit.Core.Installation;

namespace SecurityToolkit.Core.Tests.Installation;

public class ToolkitReleaseResolverTests
{
    public const string WinHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string LinuxHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string ArmHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    public static string ChecksumFile =>
        $"{WinHash.ToUpperInvariant()}  security-toolkit-win-x64.exe\n" +
        $"{LinuxHash}  security-toolkit-linux-x64\r\n" +
        $"{ArmHash} *security-toolkit-linux-arm64\n";

    public static FakeHttpMessageHandler Handler(HttpStatusCode status, string body)
        => new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    [Fact]
    public async Task ResolveAsync_ParsesSha256SumsIntoLowercaseHashPerRid()
    {
        var handler = Handler(HttpStatusCode.OK, ChecksumFile);
        var resolver = new ToolkitReleaseResolver(new HttpClient(handler));

        var hashes = await resolver.ResolveAsync("https://example.test/releases/download/", "1.2.0");

        Assert.Equal(new Uri("https://example.test/releases/download/v1.2.0/SHA256SUMS"), Assert.Single(handler.RequestedUris));
        Assert.Equal(WinHash, hashes["win-x64"]);
        Assert.Equal(LinuxHash, hashes["linux-x64"]);
        Assert.Equal(ArmHash, hashes["linux-arm64"]);
    }

    [Fact]
    public async Task ResolveAsync_MissingAsset_Throws()
    {
        var resolver = new ToolkitReleaseResolver(new HttpClient(Handler(HttpStatusCode.OK, $"{WinHash}  security-toolkit-win-x64.exe\n")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("https://example.test", "1.2.0"));
        Assert.Contains("security-toolkit-linux-x64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_MalformedLine_Throws()
    {
        var resolver = new ToolkitReleaseResolver(new HttpClient(Handler(HttpStatusCode.OK, "<html>not found</html>")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("https://example.test", "1.2.0"));
    }

    [Fact]
    public async Task ResolveAsync_HttpError_ThrowsWithStatusCode()
    {
        var resolver = new ToolkitReleaseResolver(new HttpClient(Handler(HttpStatusCode.NotFound, string.Empty)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("https://example.test", "9.9.9"));
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }
}
