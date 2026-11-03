using SecurityToolkit.Core.Installation;
using System.Net;
using System.Text;

namespace SecurityToolkit.Core.Tests.Installation;

public class SemgrepRuleDownloaderTests : IDisposable
{
    private readonly string _root;
    private readonly string _securityDirectory;

    public SemgrepRuleDownloaderTests()
    {
        _root = Directory.CreateTempSubdirectory("semgrep-rule-downloader-tests-").FullName;
        _securityDirectory = Path.Combine(_root, ".security");
        Directory.CreateDirectory(_securityDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static HttpClient ClientReturning(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        });
        return new HttpClient(handler);
    }

    [Fact]
    public async Task DownloadAsync_ValidYamlResponse_WritesSnapshotAndReturnsMessage()
    {
        var client = ClientReturning("rules:\n  - id: test-rule\n    pattern: foo\n");
        var downloader = new SemgrepRuleDownloader(client);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        var messages = await downloader.DownloadAsync(
            _root, _securityDirectory, new[] { "p/csharp" }, new Dictionary<string, string>(), backup);

        var path = Path.Combine(_securityDirectory, "rules", "p-csharp.yml");
        Assert.True(File.Exists(path));
        Assert.Contains("rules:", File.ReadAllText(path));
        Assert.Single(messages);
        Assert.Contains("p/csharp", messages[0]);
    }

    [Fact]
    public async Task DownloadAsync_ValidJsonResponse_WritesSnapshot()
    {
        var client = ClientReturning("""{"rules":[{"id":"test-rule"}]}""");
        var downloader = new SemgrepRuleDownloader(client);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await downloader.DownloadAsync(_root, _securityDirectory, new[] { "p/typescript" }, new Dictionary<string, string>(), backup);

        Assert.True(File.Exists(Path.Combine(_securityDirectory, "rules", "p-typescript.yml")));
    }

    [Fact]
    public async Task DownloadAsync_EmptyJsonRulesArray_Throws()
    {
        var client = ClientReturning("""{"rules":[]}""");
        var downloader = new SemgrepRuleDownloader(client);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync(_root, _securityDirectory, new[] { "p/csharp" }, new Dictionary<string, string>(), backup));
    }

    [Fact]
    public async Task DownloadAsync_HtmlErrorPage_Throws()
    {
        var client = ClientReturning("<!DOCTYPE html><html><body>Login required</body></html>");
        var downloader = new SemgrepRuleDownloader(client);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync(_root, _securityDirectory, new[] { "p/csharp" }, new Dictionary<string, string>(), backup));

        Assert.Contains("not a YAML/JSON", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_PlainTextWithoutRulesKey_Throws()
    {
        var client = ClientReturning("this is not yaml at all, just prose text.");
        var downloader = new SemgrepRuleDownloader(client);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync(_root, _securityDirectory, new[] { "p/csharp" }, new Dictionary<string, string>(), backup));
    }

    [Fact]
    public async Task DownloadAsync_InvalidRulesetName_Throws()
    {
        var client = ClientReturning("rules:\n");
        var downloader = new SemgrepRuleDownloader(client);
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync(_root, _securityDirectory, new[] { "not valid!" }, new Dictionary<string, string>(), backup));
    }

    [Fact]
    public async Task DownloadAsync_ExistingSnapshotMatchingHash_SkipsRedownload()
    {
        var rulesDirectory = Path.Combine(_securityDirectory, "rules");
        Directory.CreateDirectory(rulesDirectory);
        var path = Path.Combine(rulesDirectory, "p-csharp.yml");
        File.WriteAllText(path, "rules:\n  - id: existing\n");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Should not be called."));
        var downloader = new SemgrepRuleDownloader(new HttpClient(handler));
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        var priorHashes = new Dictionary<string, string> { [".security/rules/p-csharp.yml"] = hash };
        var messages = await downloader.DownloadAsync(_root, _securityDirectory, new[] { "p/csharp" }, priorHashes, backup);

        Assert.Empty(messages);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task DownloadAsync_ExistingSnapshotOnlyLineEndingsChangedByGitCheckout_SkipsRedownloadWithoutThrowing()
    {
        var rulesDirectory = Path.Combine(_securityDirectory, "rules");
        Directory.CreateDirectory(rulesDirectory);
        var path = Path.Combine(rulesDirectory, "p-csharp.yml");
        // Hash recorded when the LF snapshot was downloaded; core.autocrlf later checked it out as CRLF.
        var lfHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("rules:\n  - id: existing\n")));
        File.WriteAllText(path, "rules:\r\n  - id: existing\r\n");

        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Should not be called."));
        var downloader = new SemgrepRuleDownloader(new HttpClient(handler));
        var backup = new BackupService(_root, _securityDirectory, "run-1");

        var priorHashes = new Dictionary<string, string> { [".security/rules/p-csharp.yml"] = lfHash };
        var messages = await downloader.DownloadAsync(_root, _securityDirectory, new[] { "p/csharp" }, priorHashes, backup);

        Assert.Empty(messages);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task DownloadAsync_ExistingSnapshotManuallyModified_Throws()
    {
        var rulesDirectory = Path.Combine(_securityDirectory, "rules");
        Directory.CreateDirectory(rulesDirectory);
        var path = Path.Combine(rulesDirectory, "p-csharp.yml");
        File.WriteAllText(path, "rules:\n  - id: manually-edited\n");

        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Should not be called."));
        var downloader = new SemgrepRuleDownloader(new HttpClient(handler));
        var priorHashes = new Dictionary<string, string>();
        var backup = new BackupService(_root, _securityDirectory, "run-1", priorHashes);

        // A prior install recorded no hash for this exact content -> looks manually modified.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => downloader.DownloadAsync(_root, _securityDirectory, new[] { "p/csharp" }, priorHashes, backup));
    }

    [Fact]
    public async Task DownloadAsync_FreshCloneWithCommittedSnapshot_KeepsItWithoutDownloading()
    {
        var rulesDirectory = Path.Combine(_securityDirectory, "rules");
        Directory.CreateDirectory(rulesDirectory);
        var path = Path.Combine(rulesDirectory, "p-csharp.yml");
        File.WriteAllText(path, "rules:\n  - id: committed\n");

        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Should not be called."));
        var downloader = new SemgrepRuleDownloader(new HttpClient(handler));
        // No install-state.json on this checkout: priorHashes is null.
        var backup = new BackupService(_root, _securityDirectory, "run-1", priorHashes: null);

        var messages = await downloader.DownloadAsync(_root, _securityDirectory, new[] { "p/csharp" }, new Dictionary<string, string>(), backup);

        Assert.Empty(messages);
        Assert.Equal("rules:\n  - id: committed\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("﻿rules:\n  - id: x\n")]
    [InlineData("rules:\r\n  - id: x\r\n")]
    public void NormalizeRuleSnapshot_StripsBomAndNormalizesCrlf(string input)
    {
        var normalized = SemgrepRuleDownloader.NormalizeRuleSnapshot(input);

        Assert.DoesNotContain('﻿', normalized);
        Assert.DoesNotContain("\r\n", normalized, StringComparison.Ordinal);
    }
}
