namespace Nachos.Infra.Tests;

/// <summary>
/// Runs the real hooks (<c>postprovision</c>/<c>postdeploy</c>; <c>.sh</c> under bash, <c>.ps1</c> under pwsh) against
/// fake <c>az</c>, <c>sqlcmd</c>, <c>curl</c>, <c>dotnet</c> and <c>sleep</c> executables placed first on PATH, so the
/// scripts' control flow can be exercised without any Azure access. PATH is reduced to the fake directory plus the system directories; a real <c>az</c> installed
/// elsewhere is never reachable, and every fake call is recorded in a log.
/// </summary>
internal sealed class FakeToolbox : IDisposable
{
    /// <summary>What the fake <c>az keyvault secret download</c> writes as the existing signing secret.</summary>
    public const string SigningMaterial = "fake-signing-material";

    /// <summary>The SID the fake <c>sqlcmd</c> computes from the managed identity's client id.</summary>
    public const string ExpectedSid = "0x0123456789ABCDEF0123456789ABCDEF";

    private readonly string directory = Directory.CreateTempSubdirectory("nachos-fakes-").FullName;

    public FakeToolbox()
    {
        Log = Path.Combine(directory, "calls.log");
        File.WriteAllText(Log, string.Empty);

        Install("az", $$"""
            echo "az $*" >> "$FAKE_LOG"
            case "$*" in
              *"keyvault secret list"*)
                calls="$(grep -c 'keyvault secret list' "$FAKE_LOG")"
                if [[ "${FAKE_LIST_ERROR:-}" == other ]]; then echo "(ResourceNotFound) vault missing" >&2; exit 1; fi
                if (( calls <= ${FAKE_LIST_FORBIDDEN:-0} )); then echo "(Forbidden) ForbiddenByRbac" >&2; exit 1; fi
                for name in ${FAKE_LIST_NAMES:-}; do echo "$name"; done
                ;;
              *"keyvault secret download"*)
                while (( $# )); do if [[ "$1" == --file ]]; then echo "{{SigningMaterial}}" > "$2"; fi; shift; done
                ;;
              *"keyvault secret set"*)
                # Keep exactly what would be stored, so tests can assert on the secret's content.
                name=""; file=""
                while (( $# )); do
                  case "$1" in --name) name="$2"; shift ;; --file) file="$2"; shift ;; esac
                  shift
                done
                cp "$file" "$FAKE_DIR/secret-$name"
                ;;
            esac
            exit 0
            """);
        Install("sqlcmd", $$"""
            echo "sqlcmd $*" >> "$FAKE_LOG"
            case "$*" in
              # The existing user's SID (empty: no such user yet).
              *"CONVERT(varchar(34), sid, 1)"*) if [[ -n "${FAKE_EXISTING_SID:-}" ]]; then echo "$FAKE_EXISTING_SID"; fi ;;
              # The SID computed from the client id.
              *"CONVERT(varchar(34), CAST"*) echo {{ExpectedSid}} ;;
            esac
            exit 0
            """);
        Install("curl", """
            echo "curl $*" >> "$FAKE_LOG"
            if [[ "$*" == *"api.ipify.org"* ]]; then echo 203.0.113.7; exit 0; fi
            if [[ "${FAKE_CURL_FAIL:-}" == 1 ]]; then echo "curl: (7) Failed to connect" >&2; exit 7; fi
            # The n-th readiness GET answers body n; once the script runs out, the last body repeats.
            n="$(grep -c '^curl ' "$FAKE_LOG")"
            last="$(ls "$FAKE_DIR"/curl-body-* | wc -l)"
            (( n > last )) && n="$last"
            cat "$FAKE_DIR/curl-body-$n"
            exit 0
            """);
        Install("dotnet", """
            echo "dotnet $*" >> "$FAKE_LOG"
            if [[ "$*" == *"keys create"* ]]; then printf '%b' "${FAKE_KEY_OUTPUT:-}"; fi
            exit 0
            """);
        Install("sleep", "echo \"sleep $*\" >> \"$FAKE_LOG\"; exit 0");
    }

    public string Log { get; }

    public IReadOnlyList<string> Calls => File.ReadAllLines(Log);

    public int Count(string fragment) => Calls.Count(line => line.Contains(fragment, StringComparison.Ordinal));

    /// <summary>The contents of the <c>--file</c> the hook last stored under <paramref name="name"/>, or null if it stored none.</summary>
    public string? StoredSecret(string name)
    {
        var path = Path.Combine(directory, "secret-" + name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public ProcessResult RunPostprovision(
        string? keyOutput = null, int forbiddenListings = 0, string? listError = null, string listNames = "", string existingSid = "") =>
        Tools.Run(
            Tools.Require("bash"),
            [RepoPaths.Combine("infra/hooks/postprovision.sh")],
            environment: PostprovisionEnvironment(keyOutput, forbiddenListings, listError, listNames, existingSid));

    /// <summary>
    /// Runs the real <c>postprovision.ps1</c> under <c>pwsh</c> with the same fakes. <c>Invoke-RestMethod</c> (the
    /// public-IP lookup) and <c>Start-Sleep</c> are replaced by functions in the wrapper; see <see cref="RunPowerShell"/>.
    /// </summary>
    public ProcessResult RunPostprovisionPs1(string? keyOutput = null, string listNames = "", string existingSid = "") =>
        RunPowerShell("infra/hooks/postprovision.ps1", PostprovisionEnvironment(keyOutput, 0, null, listNames, existingSid));

    /// <summary>Runs <c>postdeploy.sh</c>; the fake <c>curl</c> answers the n-th readiness GET with <paramref name="bodies"/>[n].</summary>
    public ProcessResult RunPostdeploy(IReadOnlyList<string> bodies, bool curlFails = false)
    {
        for (var i = 0; i < bodies.Count; i++)
        {
            File.WriteAllText(Path.Combine(directory, $"curl-body-{i + 1}"), bodies[i]);
        }

        var environment = new Dictionary<string, string>
        {
            ["PATH"] = FakePath,
            ["FAKE_LOG"] = Log,
            ["FAKE_DIR"] = directory,
            ["FAKE_CURL_FAIL"] = curlFails ? "1" : string.Empty,
            ["NACHOS_API_URI"] = "https://nachos-api.example.test",
        };

        return Tools.Run(Tools.Require("bash"), [RepoPaths.Combine("infra/hooks/postdeploy.sh")], environment: environment);
    }

    /// <summary>Runs the real <c>postdeploy.ps1</c> against <paramref name="apiUri"/> (a local test listener).</summary>
    public ProcessResult RunPostdeployPs1(Uri apiUri) =>
        RunPowerShell("infra/hooks/postdeploy.ps1", new Dictionary<string, string>
        {
            ["PATH"] = FakePath,
            ["FAKE_LOG"] = Log,
            ["NACHOS_API_URI"] = apiUri.ToString().TrimEnd('/'),
        });

    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>
    /// Invokes the hook, unmodified, from a wrapper that first defines two functions. PowerShell resolves functions
    /// before cmdlets, so the hook's <c>Start-Sleep</c> only records the delay and its <c>Invoke-RestMethod</c> (used
    /// only for the api.ipify.org lookup) returns a documentation address instead of reaching the internet. The shipped
    /// scripts carry no test seam. A terminating error in the hook makes the wrapper exit 1. pwsh starts with the
    /// normal PATH (a dotnet-tool pwsh launches the real <c>dotnet</c>) and switches to the fake PATH before the hook.
    /// </summary>
    private static ProcessResult RunPowerShell(string hook, Dictionary<string, string> environment)
    {
        const string Wrapper =
            "$env:PATH = $env:FAKE_PATH\n" +
            "function Start-Sleep { param([double] $Seconds) Add-Content -LiteralPath $env:FAKE_LOG -Value \"sleep $Seconds\" }\n" +
            "function Invoke-RestMethod { param([string] $Uri, [int] $TimeoutSec)\n" +
            "  if ($Uri -ne 'https://api.ipify.org') { throw \"unexpected Invoke-RestMethod $Uri\" }\n" +
            "  '203.0.113.7' }\n" +
            "try { & $env:NACHOS_HOOK } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }\n";

        environment["NACHOS_HOOK"] = RepoPaths.Combine(hook);
        environment["FAKE_PATH"] = environment["PATH"];
        environment.Remove("PATH");
        // The readiness GET goes to a 127.0.0.1 listener; never through an HTTP proxy from the environment.
        environment["NO_PROXY"] = "127.0.0.1,localhost";
        environment["no_proxy"] = "127.0.0.1,localhost";
        return Tools.Run(Tools.Require("pwsh"), ["-NoProfile", "-NonInteractive", "-Command", Wrapper], environment: environment);
    }

    private string FakePath => directory + Path.PathSeparator + "/usr/bin" + Path.PathSeparator + "/bin";

    private Dictionary<string, string> PostprovisionEnvironment(
        string? keyOutput, int forbiddenListings, string? listError, string listNames, string existingSid) => new()
    {
        ["PATH"] = FakePath,
        ["FAKE_LOG"] = Log,
        ["FAKE_DIR"] = directory,
        ["FAKE_EXISTING_SID"] = existingSid,
        ["FAKE_LIST_FORBIDDEN"] = forbiddenListings.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["FAKE_LIST_ERROR"] = listError ?? string.Empty,
        ["FAKE_LIST_NAMES"] = listNames,
        ["FAKE_KEY_OUTPUT"] = keyOutput ?? string.Empty,
        ["AZURE_SUBSCRIPTION_ID"] = "00000000-0000-0000-0000-0000000000aa",
        ["AZURE_RESOURCE_GROUP"] = "rg-test",
        ["AZURE_KEY_VAULT_NAME"] = "kv-test",
        ["AZURE_SQL_SERVER_NAME"] = "sql-test",
        ["AZURE_SQL_SERVER_FQDN"] = "sql-test.database.windows.net",
        ["AZURE_SQL_DATABASE_NAME"] = "nachos",
        ["AZURE_MANAGED_IDENTITY_NAME"] = "id-test",
        ["AZURE_MANAGED_IDENTITY_CLIENT_ID"] = "11111111-1111-1111-1111-111111111111",
    };

    private void Install(string name, string body)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/usr/bin/env bash\n" + body.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
