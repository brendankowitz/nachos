namespace Nachos.DocsGen;

internal static class AtomicOutputs
{
    internal static readonly string[] Names = ["cli.md", "compatibility.json", "options.md", "rest-api.json"];

    internal static void Publish(string destination, Dictionary<string, byte[]> files)
    {
        if (!files.Keys.Order(StringComparer.Ordinal).SequenceEqual(Names, StringComparer.Ordinal) ||
            files.Values.Any(bytes => bytes.Length == 0)) throw new InvalidDataException();
        Inputs.RejectLinks(destination);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var lockPath = destination + ".docsgen-lock";
        using var gate = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose);
        if (Directory.Exists(destination) &&
            Directory.EnumerateFileSystemEntries(destination).Any(entry => Directory.Exists(entry) ||
                !Names.Contains(Path.GetFileName(entry), StringComparer.Ordinal) ||
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException();
        var stage = destination + ".docsgen-stage-" + Guid.NewGuid().ToString("N");
        var backup = destination + ".docsgen-backup-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        var movedOld = false;
        var published = false;
        try
        {
            foreach (var (name, bytes) in files)
            {
                var path = Path.Combine(stage, name);
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new IOException();
            }
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                movedOld = true;
            }
            Directory.Move(stage, destination);
            published = true;
        }
        finally
        {
            if (!published && movedOld) Directory.Move(backup, destination);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
        if (movedOld) Directory.Delete(backup, recursive: true);
    }
}
