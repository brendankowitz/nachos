using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nachos.LicenseCheck;

internal static class NpmArchives
{
    private const long MaxCompressedBytes = 128L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxEvidenceBytes = 2 * 1024 * 1024;
    private const int MaxEntries = 20000;

    public static Dictionary<string, string> Read(JsonElement locked, string name, string cache, out string archivePath, string? declared = null)
        => Read(locked, name, cache, out archivePath, out _, declared);

    public static Dictionary<string, string> Read(JsonElement locked, string name, string cache, out string archivePath, out string archiveRoot, string? declared = null)
    {
        var (_, digest, filename) = Descriptor(locked, name);
        archivePath = Collectors.Under(cache, filename);
        using var file = File.OpenRead(archivePath);
        Verify(file, digest);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var bounded = new BoundedReadStream(gzip, MaxExpandedBytes);
        using var tar = new TarReader(bounded);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var basename = name.Split('/')[^1];
        var typesRoot = name.StartsWith("@types/", StringComparison.Ordinal)
            ? basename + " v" + string.Join('.', Collectors.RequiredString(locked, "version").Split('.').Take(2)) : null;
        archiveRoot = "";
        while (tar.GetNextEntry() is { } entry)
        {
            if (seen.Count >= MaxEntries)
            {
                throw new InvalidDataException("npm archive entry count limit exceeded.");
            }
            var pathInArchive = entry.Name.TrimEnd('/');
            var root = pathInArchive.Split('/')[0];
            if (archiveRoot.Length == 0) archiveRoot = root;
            if (root != archiveRoot || root != "package" && root != basename && root != typesRoot
                || pathInArchive.Contains('\\', StringComparison.Ordinal) || pathInArchive.Contains(':', StringComparison.Ordinal)
                || pathInArchive.Split('/').Any(segment => segment is "." or ".." or "")
                || !seen.Add(pathInArchive))
            {
                throw new InvalidDataException($"Unsafe or duplicate npm archive path: {entry.Name}");
            }
            if (entry.EntryType == TarEntryType.Directory)
            {
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length > 0 && entry.DataStream is null)
            {
                throw new InvalidDataException($"Unsafe linked/special npm archive entry: {entry.Name}");
            }
            if (!pathInArchive.StartsWith(archiveRoot + "/", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsafe npm archive file path: {entry.Name}");
            }
            var relative = pathInArchive[(archiveRoot.Length + 1)..];
            var evidence = relative == "package.json" || relative == declared
                || LicenseText.IsImplicitNpmDocument(relative);
            if (!evidence)
            {
                continue;
            }
            if (relative != "package.json" && !LicenseText.IsDocumentationPath(relative))
            {
                throw new InvalidDataException($"unsupported license entry (not a documentation file): {relative}");
            }
            if (entry.Length > MaxEvidenceBytes)
            {
                throw new InvalidDataException($"npm archive evidence size limit exceeded: {relative}");
            }
            if (entry.Length == 0)
            {
                result.Add(relative, "");
                continue;
            }
            using var reader = new StreamReader(entry.DataStream!, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            result.Add(relative, reader.ReadToEnd());
        }
        bounded.CopyTo(Stream.Null);
        if (!result.ContainsKey("package.json"))
        {
            throw new InvalidDataException("npm archive package.json missing.");
        }
        return result;
    }

    public static async Task FetchAsync(string lockPath, string cache)
    {
        var records = NpmLock.Read(lockPath);
        var directory = Path.GetDirectoryName(lockPath)!;
        await FetchRecordsAsync(records.Where(record => record.Key.Length > 0
            && !Directory.Exists(Collectors.Under(directory, record.Key)))
            .Select(record => (NpmLock.Name(record.Key), record.Value)), cache).ConfigureAwait(false);
    }

    public static async Task FetchRecordsAsync(IEnumerable<(string Name, JsonElement Locked)> records, string cache)
    {
        var downloads = records.Select(record => (record.Name, record.Locked, Descriptor(record.Locked, record.Name))).ToArray();
        Directory.CreateDirectory(cache);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        foreach (var (name, locked, descriptor) in downloads)
        {
            var (uri, digest, filename) = descriptor;
            var path = Collectors.Under(cache, filename);
            if (File.Exists(path))
            {
                Verify(path, digest);
                continue;
            }
            var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxCompressedBytes)
                {
                    throw new InvalidDataException($"npm registry download rejected ({response.StatusCode}): {uri}");
                }
                await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                    {
                        total += count;
                        if (total > MaxCompressedBytes)
                        {
                            throw new InvalidDataException("npm archive compressed size limit exceeded.");
                        }
                        await target.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    }
                }
                Verify(partial, digest);
                File.Move(partial, path);
                Console.WriteLine($"Downloaded verified npm evidence: {name}@{Collectors.RequiredString(locked, "version")}");
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
        }
    }

    internal static (Uri Uri, byte[] Digest, string Filename) Descriptor(JsonElement locked, string name)
    {
        var version = Collectors.RequiredString(locked, "version");
        var resolved = Collectors.RequiredString(locked, "resolved");
        var expectedPath = "/" + name + "/-/" + name.Split('/')[^1] + "-" + version + ".tgz";
        if (!Uri.TryCreate(resolved, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "registry.npmjs.org" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || Uri.UnescapeDataString(uri.AbsolutePath) != expectedPath
            || resolved.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected npm registry provenance: {resolved}");
        }
        var integrity = Collectors.RequiredString(locked, "integrity");
        if (!integrity.StartsWith("sha512-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("npm archive requires a locked sha512 integrity value.");
        }
        byte[] digest;
        try
        {
            digest = Convert.FromBase64String(integrity["sha512-".Length..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Malformed npm archive integrity value.", exception);
        }
        if (digest.Length != 64)
        {
            throw new InvalidDataException("Malformed npm archive sha512 integrity length.");
        }
        return (uri, digest, Convert.ToHexString(SHA256.HashData(digest)).ToLowerInvariant() + ".tgz");
    }

    private static void Verify(string path, byte[] digest)
    {
        using var file = File.OpenRead(path);
        Verify(file, digest);
    }

    private static void Verify(Stream file, byte[] digest)
    {
        if (file.Length > MaxCompressedBytes)
        {
            throw new InvalidDataException("npm archive compressed size limit exceeded.");
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(file), digest))
        {
            throw new InvalidDataException("npm archive integrity mismatch.");
        }
        file.Position = 0;
    }
}

internal sealed class BoundedReadStream(Stream inner, long limit) : Stream
{
    private long count;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => count; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int length) => Read(buffer.AsSpan(offset, length));
    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        count += read;
        if (count > limit)
        {
            throw new InvalidDataException("npm archive expanded size limit exceeded.");
        }
        return read;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
}
