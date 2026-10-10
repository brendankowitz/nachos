using System.Security.Cryptography;
using System.Text.Json;

namespace Nachos.LicenseCheck.Tests;

internal static class DocsFixtureBuilder
{
    public static void Seal(AuditFixture fixture, params DocsPackage[] emitted)
    {
        fixture.WriteText("docs/site/astro.config.mjs", "// controlled fixture, never executed");
        fixture.WriteText("docs/site/tsconfig.json", "{}");
        fixture.WriteText("docs/site/.npmrc", "");
        fixture.WriteText("docs/site/src/index.html", "<html>controlled fixture</html>");
        fixture.WriteText("docs/site/public/favicon.svg", "<svg/>");
        fixture.WriteText("docs/site/integrations/provenance.mjs", "// controlled fixture");
        fixture.WriteText("docs/site/scripts/build.mjs", "// controlled fixture");
        fixture.WriteText("docs/assets/logo.svg", "<svg/>");
        fixture.WriteText("docs/site/dist/index.html", "<html>controlled fixture</html>");
        var site = fixture.Full("docs/site");
        var inputs = new List<(string Path, string File)>();
        foreach (var file in new[] { ".npmrc", "package.json", "package-lock.json", "astro.config.mjs", "tsconfig.json" })
            inputs.Add(("site/" + file, Path.Combine(site, file)));
        foreach (var tree in new[] { "src", "public", "integrations", "scripts" })
            inputs.AddRange(Directory.EnumerateFiles(Path.Combine(site, tree), "*", SearchOption.AllDirectories)
                .Select(file => ("site/" + Path.GetRelativePath(site, file).Replace('\\', '/'), file)));
        inputs.AddRange(Directory.EnumerateFiles(fixture.Full("docs/assets"), "*", SearchOption.AllDirectories)
            .Select(file => ("assets/" + Path.GetRelativePath(fixture.Full("docs/assets"), file).Replace('\\', '/'), file)));
        var sources = new List<object>
        {
            new { path = "site/src/index.html", sha256 = Hash(fixture.Full("docs/site/src/index.html")), kind = "first-party" }
        };
        var refs = new List<string> { "site/src/index.html" };
        foreach (var package in emitted.OrderBy(item => item.Package, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal))
        {
            var packagePath = "node_modules/" + package.Package;
            var file = "npm/" + packagePath + "/package.json";
            var hash = Hash(fixture.Full("docs/site/" + packagePath + "/package.json"));
            refs.Add(file);
            sources.Add(new { path = file, sha256 = hash, kind = "package", package = package.Package, version = package.Version,
                packagePath, packageJsonSha256 = hash });
        }
        var sourceJson = JsonSerializer.SerializeToElement(sources).EnumerateArray()
            .OrderBy(item => item.GetProperty("path").GetString(), StringComparer.Ordinal).ToArray();
        var packages = emitted.OrderBy(item => item.Package, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal)
            .Select(item => new { package = item.Package, version = item.Version }).ToArray();
        fixture.Write("docs/site/dist/.nachos/bundle-modules.json", packages);
        fixture.Write("docs/site/dist/.nachos/output-provenance.v1.json", new
        {
            schemaVersion = 1, build = new { site = "https://brendankowitz.github.io", @base = "/nachos" },
            inputs = inputs.OrderBy(item => item.Path, StringComparer.Ordinal).Select(item => new { path = item.Path, sha256 = Hash(item.File) }),
            sources = sourceJson,
            outputs = new[] { new
            {
                path = "index.html", sha256 = Hash(fixture.Full("docs/site/dist/index.html")), packages,
                evidence = new[] { new { producer = "astro-render", sources = refs.Order(StringComparer.Ordinal).ToArray(),
                    sha256 = Hash(fixture.Full("docs/site/src/index.html")) } }
            } }
        });
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
