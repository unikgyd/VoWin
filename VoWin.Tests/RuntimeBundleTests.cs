using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoWin.Services;

namespace VoWin.Tests;

public sealed class RuntimeBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoWin bundle 中文 " + Guid.NewGuid().ToString("N"));

    private static MemoryStream Bundle(string path = "host-ims/helper.exe", string contents = "test executable",
        string? extraPath = null, bool wrongHash = false)
    {
        var bytes = Encoding.UTF8.GetBytes(contents);
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var output = zip.CreateEntry(path).Open()) output.Write(bytes);
            if (extraPath != null)
                using (var output = zip.CreateEntry(extraPath).Open()) output.Write(bytes);
            var manifest = new RuntimeBundleManifest(1,
                [new(path, bytes.Length, wrongHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)))]);
            using var json = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(json, manifest);
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task ExtractsAndReusesVerifiedContentAtPathWithSpacesAndUnicode()
    {
        using var bundle = Bundle();
        var path = await RuntimeBundleExtractor.ExtractAsync(bundle, _root);
        Assert.Equal("test executable", await File.ReadAllTextAsync(Path.Combine(path, "host-ims/helper.exe")));
        Assert.Equal(path, await RuntimeBundleExtractor.ExtractAsync(bundle, _root));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task RepairsModifiedCacheAndKeepsRecoverableCopy()
    {
        using var bundle = Bundle();
        var path = await RuntimeBundleExtractor.ExtractAsync(bundle, _root);
        await File.WriteAllTextAsync(Path.Combine(path, "host-ims/helper.exe"), "tampered");
        Assert.Equal(path, await RuntimeBundleExtractor.ExtractAsync(bundle, _root));
        Assert.Equal("test executable", await File.ReadAllTextAsync(Path.Combine(path, "host-ims/helper.exe")));
        Assert.Single(Directory.GetDirectories(_root, "*.invalid-*"));
    }

    [Fact]
    public async Task RepairsCacheWithExtraNativeLibrary()
    {
        using var bundle = Bundle();
        var path = await RuntimeBundleExtractor.ExtractAsync(bundle, _root);
        await File.WriteAllTextAsync(Path.Combine(path, "host-ims/untrusted.dll"), "extra");
        await RuntimeBundleExtractor.ExtractAsync(bundle, _root);
        Assert.False(File.Exists(Path.Combine(path, "host-ims/untrusted.dll")));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("/escape.exe")]
    [InlineData("host-ims\\helper.exe")]
    [InlineData("host-ims/helper.exe:evil")]
    [InlineData("host-ims/../helper.exe")]
    [InlineData("host-ims/helper.exe.")]
    public async Task RejectsUnsafeArchivePaths(string unsafePath)
    {
        using var bundle = Bundle(unsafePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeBundleExtractor.ExtractAsync(bundle, _root));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task RejectsUnlistedFiles()
    {
        using var bundle = Bundle(extraPath: "unlisted.dll");
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeBundleExtractor.ExtractAsync(bundle, _root));
    }

    [Fact]
    public async Task RejectsWrongHashWithoutPublishingPartialCache()
    {
        using var bundle = Bundle(wrongHash: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeBundleExtractor.ExtractAsync(bundle, _root));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task ConcurrentExtractorsPublishOneCache()
    {
        using var first = Bundle();
        using var second = Bundle();
        var results = await Task.WhenAll(RuntimeBundleExtractor.ExtractAsync(first, _root),
            RuntimeBundleExtractor.ExtractAsync(second, _root));
        Assert.Equal(results[0], results[1]);
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task CancellationDoesNotPublishPartialCache()
    {
        using var bundle = Bundle();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RuntimeBundleExtractor.ExtractAsync(bundle, _root, ct: cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
