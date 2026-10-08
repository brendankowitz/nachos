using System.Text.Json;
using Nachos.LicenseCheck;

try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    var fetch = args.FirstOrDefault() == "fetch-npm";
    var known = new HashSet<string>(fetch ? ["--lock", "--npm-archives"]
        : ["--repo", "--nuget-inventory", "--nuget-cache", "--api-publish", "--cli-publish", "--python-archives", "--npm-archives", "--report"], StringComparer.Ordinal);
    for (var index = fetch ? 1 : 0; index < args.Length; index += 2)
    {
        if (!known.Contains(args[index]) || index + 1 >= args.Length || !options.TryAdd(args[index], args[index + 1]))
        {
            throw new InvalidDataException($"Unknown, duplicate, or missing argument value: {args[index]}");
        }
    }
    string Required(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? Path.GetFullPath(value) : throw new InvalidDataException($"Required argument: {name}");
    if (fetch)
    {
        await NpmArchives.FetchAsync(Required("--lock"), Required("--npm-archives"));
        return 0;
    }
    var inputs = new AuditInputs(Required("--repo"), Required("--nuget-inventory"), Required("--nuget-cache"),
        Required("--api-publish"), Required("--cli-publish"), options.GetValueOrDefault("--python-archives"), options.GetValueOrDefault("--npm-archives"));
    var reportPath = Required("--report");
    var report = LicenseAudit.Run(inputs);
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
    }));
    foreach (var error in report.Errors)
    {
        Console.Error.WriteLine("LICENSE ERROR: " + error);
    }
    Console.WriteLine($"{report.Packages.Count} package instances accepted; {report.Errors.Count} errors. Report: {reportPath}");
    return report.Errors.Count == 0 ? 0 : 1;
}
catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
    or JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException or OperationCanceledException)
{
    Console.Error.WriteLine("LICENSE INPUT ERROR: " + exception.Message);
    return 2;
}
