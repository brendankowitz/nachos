namespace Nachos.Infra.Tests;

/// <summary>
/// Runs the real <c>postprovision.sh</c> against fake <c>az</c>, <c>sqlcmd</c>, <c>curl</c>, <c>dotnet</c> and
/// <c>sleep</c> executables placed first on PATH, so the script's control flow can be exercised without any
/// Azure access. PATH is reduced to the fake directory plus the system directories; a real <c>az</c> installed
/// elsewhere is never reachable, and every fake call is recorded in a log.
/// </summary>
internal sealed class FakeToolbox : IDisposable
{
    /// <summary>What the fake <c>az keyvault secret download</c> writes as the existing signing secret.</summary>
    public const string SigningMaterial = "fake-signing-material";

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
        Install("sqlcmd", """
            echo "sqlcmd $*" >> "$FAKE_LOG"
            if [[ "$*" == *"CONVERT(varchar(34)"* ]]; then echo 0x0123456789ABCDEF0123456789ABCDEF; fi
            exit 0
            """);
        Install("curl", "echo 203.0.113.7");
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

    public ProcessResult RunPostprovision(string? keyOutput = null, int forbiddenListings = 0, string? listError = null, string listNames = "")
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = directory + Path.PathSeparator + "/usr/bin" + Path.PathSeparator + "/bin",
            ["FAKE_LOG"] = Log,
            ["FAKE_DIR"] = directory,
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

        return Tools.Run(Tools.Require("bash"), [RepoPaths.Combine("infra/hooks/postprovision.sh")], environment: environment);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

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
