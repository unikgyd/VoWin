using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace VoWin.Services;

/// <summary>Versioned, verified private runtime. Never searches PATH for bundled tools.</summary>
public static class BundledRuntime
{
    public const string ResourceName = "VoWin.Runtime.zip";
    public static string? Root { get; private set; }
    public static bool IsAvailable => Root != null;
    public static bool IsEmbedded => typeof(BundledRuntime).Assembly.GetManifestResourceInfo(ResourceName) != null;
    public static string? Find(string relativePath) => Root is { } root &&
        File.Exists(Path.Combine(root, relativePath)) ? Path.Combine(root, relativePath) : null;

    public static string? FindHostIms(string name) => Find(Path.Combine("host-ims", name)) ??
        (File.Exists(Path.Combine(AppContext.BaseDirectory, "host-ims", name))
            ? Path.Combine(AppContext.BaseDirectory, "host-ims", name) : null);

    public static bool HasModemDrivers => Root is { } root && Directory.Exists(Path.Combine(root, "drivers")) &&
        Directory.EnumerateFiles(Path.Combine(root, "drivers"), "*.inf", SearchOption.AllDirectories).Any();

    public static async Task PrepareAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await using var resource = typeof(BundledRuntime).Assembly.GetManifestResourceStream(ResourceName);
        if (resource == null) return; // Ordinary source builds remain usable without redistributables.
        // Elevated helpers must never execute from a normal user's writable cache.
        using var identity = WindowsIdentity.GetCurrent();
        var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var parent = Path.Combine(Environment.GetFolderPath(admin
            ? Environment.SpecialFolder.ProgramFiles : Environment.SpecialFolder.LocalApplicationData),
            "VoWin", "Runtime");
        Root = await RuntimeBundleExtractor.ExtractAsync(resource, parent, progress, ct).ConfigureAwait(false);
    }
}

public sealed record RuntimeBundleFile(string Path, long Length, string Sha256);
public sealed record RuntimeBundleManifest(int SchemaVersion, RuntimeBundleFile[] Files);

/// <summary>All executable bytes come from the embedded resource, not a downloaded manifest.</summary>
public static class RuntimeBundleExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;

    public static async Task<string> ExtractAsync(Stream archive, string cacheParent,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!archive.CanSeek) throw new ArgumentException("The bundle stream must support seeking.", nameof(archive));
        progress?.Report("正在校验内置运行组件…");
        archive.Position = 0;
        var id = Convert.ToHexString(await SHA256.HashDataAsync(archive, ct).ConfigureAwait(false)).ToLowerInvariant();
        archive.Position = 0;
        var parent = Path.GetFullPath(cacheParent);
        AssertNoReparsePoints(parent);
        Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, id);
        // A file lock works across helper processes without crossing awaits with a thread-affine mutex.
        await using var cacheLock = await AcquireLockAsync(Path.Combine(parent, id + ".lock"), ct).ConfigureAwait(false);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("运行组件缺少清单。");
        if (manifestEntry.Length > 2 * 1024 * 1024) throw new InvalidDataException("运行组件清单过大。");
        RuntimeBundleManifest manifest;
        await using (var input = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<RuntimeBundleManifest>(input, JsonOptions, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("运行组件清单无效。");
        ValidateManifest(zip, manifest);
        if (Directory.Exists(target))
        {
            if (await VerifyAsync(target, manifest, ct).ConfigureAwait(false)) return target;
            // Keep a recoverable copy rather than overwriting files a helper may still be using.
            AssertNoReparsePoints(target);
            Directory.Move(target, target + ".invalid-" + Guid.NewGuid().ToString("N"));
        }

        var staging = Path.Combine(parent, id + ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            for (var i = 0; i < manifest.Files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = manifest.Files[i];
                progress?.Report($"首次准备运行组件 · {i + 1} / {manifest.Files.Length}");
                var destination = ResolveInside(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = zip.GetEntry(file.Path)!.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            if (!await VerifyAsync(staging, manifest, ct).ConfigureAwait(false))
                throw new InvalidDataException("内置组件完整性校验失败，请重新获取完整发布版。");
            Directory.Move(staging, target);
            progress?.Report("运行组件已就绪");
            return target;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                AssertNoReparsePoints(staging);
                Directory.Delete(staging, recursive: true); // Only this newly generated staging directory.
            }
        }
    }

    private static void ValidateManifest(ZipArchive zip, RuntimeBundleManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Files is not { Length: > 0 and < 10000 })
            throw new InvalidDataException("不支持的运行组件清单。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            ValidateRelativePath(file.Path);
            if (!names.Add(file.Path) || file.Length < 0 || file.Length > MaximumExpandedBytes ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("运行组件清单包含无效或重复项。");
            total = checked(total + file.Length);
            if (total > MaximumExpandedBytes || zip.GetEntry(file.Path)?.Length != file.Length)
                throw new InvalidDataException("运行组件大小不符合清单。");
        }
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ValidateRelativePath(entry.FullName);
            if (!entries.Add(entry.FullName) ||
                (entry.FullName != "manifest.json" && !names.Contains(entry.FullName)))
                throw new InvalidDataException("运行组件包含清单外或重复文件。");
        }
    }

    private static async Task<bool> VerifyAsync(string directory, RuntimeBundleManifest manifest, CancellationToken ct)
    {
        AssertNoReparsePoints(directory);
        foreach (var file in manifest.Files)
        {
            var path = ResolveInside(directory, file.Path);
            AssertNoReparsePoints(path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length) return false;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false))
                    .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        // Extra DLLs could be loaded by native dependency resolution; do not trust them.
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count() == manifest.Files.Length;
    }

    public static string ResolveInside(string root, string relative)
    {
        ValidateRelativePath(relative);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("运行组件路径越界。");
        return result;
    }

    private static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.StartsWith('/') || relative.Split('/').Any(part => part is "" or "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("运行组件路径无效。");
    }

    private static void AssertNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("运行组件目录不能位于符号链接或重解析点中。");
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            AssertNoReparsePoints(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(150, ct).ConfigureAwait(false); }
        }
    }
}
