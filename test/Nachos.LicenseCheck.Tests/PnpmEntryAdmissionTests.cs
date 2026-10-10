using System.Text.Json.Nodes;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class PnpmEntryAdmissionTests
{
    public static TheoryData<bool, bool, string> DependencyEntries
    {
        get
        {
            var cases = new TheoryData<bool, bool, string>();
            foreach (var shared in new[] { false, true })
                foreach (var scoped in new[] { false, true })
                    foreach (var form in new[] { "missing-file-link", "file-target", "missing-directory-link",
                        "plain-file", "valid", "escaping-directory", "escaping-file", "nonpackage-directory" })
                        cases.Add(shared, scoped, form);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(DependencyEntries))]
    public void PN4_DependencyEntriesCannotDisappearThroughDirectoryFiltering(bool shared, bool scoped, string form)
    {
        using var fixture = new PnpmRepairFixture();
        var slot = scoped ? "@scope/leaf" : "leaf";
        if (scoped) ScopeLeaf(fixture);
        var target = fixture.Install(scoped ? "@scope+leaf@1.0.0" : "leaf@1.0.0", slot, "leaf");
        var entry = fixture.Audit.Full("web/node_modules/.pnpm/"
            + (shared ? "" : "consumer@1.0.0_peer@2.0.0/") + "node_modules/" + slot);
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        var diagnostic = form switch
        {
            "missing-file-link" or "missing-directory-link" or "file-target" => "Missing pnpm dependency link target",
            "plain-file" => "Unsupported pnpm package entry",
            "escaping-directory" or "escaping-file" => "escapes evidence root",
            "nonpackage-directory" => "Installed pnpm link lacks matching physical identity/context evidence",
            _ => ""
        };
        if (form is "missing-file-link" or "missing-directory-link")
            target = fixture.Audit.Full("web/node_modules/.pnpm/absent");
        if (form == "file-target") target = Path.Combine(target, "package.json");
        if (form.StartsWith("escaping", StringComparison.Ordinal))
        {
            fixture.Audit.WriteText("outside/LICENSE", AuditFixture.Mit);
            target = fixture.Audit.Full(form == "escaping-file" ? "outside/LICENSE" : "outside");
        }
        if (form == "nonpackage-directory")
        {
            target = Path.Combine(target, "not-a-package");
            Directory.CreateDirectory(target);
        }
        if (form == "plain-file") File.WriteAllText(entry, "not a package directory");
        else if (form is "missing-file-link" or "file-target" or "escaping-file")
            File.CreateSymbolicLink(entry, target);
        else Directory.CreateSymbolicLink(entry, target);

        fixture.Save();
        var report = fixture.Audit.Check();
        if (form == "valid") report.Errors.ShouldBeEmpty();
        else report.Errors.ShouldContain(error => error.Contains(diagnostic, StringComparison.Ordinal),
            string.Join(Environment.NewLine, report.Errors));
    }

    public static TheoryData<string, string> LayoutEntries
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var level in new[] { "store", "context", "container", "shared", "package-scope", "shared-scope" })
            {
                foreach (var form in new[] { "missing-link", "file-link", "directory-link" })
                    cases.Add(level, form);
                if (level != "context") cases.Add(level, "plain-file");
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(LayoutEntries))]
    public void PN4_LayoutContainersMustBePhysicalDirectories(string level, string form)
    {
        using var fixture = new PnpmFixture();
        const string store = "web/node_modules/.pnpm";
        var relative = level switch
        {
            "store" => store,
            "context" => store + "/consumer@1.0.0_peer@2.0.0",
            "container" => store + "/consumer@1.0.0_peer@2.0.0/node_modules",
            "shared" => store + "/node_modules",
            "package-scope" => store + "/consumer@1.0.0_peer@2.0.0/node_modules/@scope",
            _ => store + "/node_modules/@scope"
        };
        var entry = fixture.Audit.Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        fixture.Audit.WriteText("layout-target/LICENSE", AuditFixture.Mit);
        if (form == "plain-file") File.WriteAllText(entry, "not a directory");
        else if (form == "directory-link")
            Directory.CreateSymbolicLink(entry, fixture.Audit.Full("layout-target"));
        else File.CreateSymbolicLink(entry, fixture.Audit.Full(form == "missing-link" ? "missing" : "layout-target/LICENSE"));

        var errors = fixture.Audit.Check().Errors;
        errors.ShouldContain(error => error.Contains(form == "plain-file" ? "Unsupported pnpm store layout" : "Linked evidence",
            StringComparison.Ordinal), string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void PN4_StoreMetadataBinAndTopLevelPackagesKeepTheirExistingScope()
    {
        using var fixture = new PnpmRepairFixture();
        fixture.Install("leaf@1.0.0", "leaf", "leaf");
        fixture.Audit.WriteText("web/node_modules/.pnpm/lock.yaml", "metadata, not a package slot");
        fixture.Audit.Write("web/node_modules/untracked/package.json", new { name = "untracked", version = "1.0.0", license = "MIT" });
        fixture.Audit.WriteText("web/node_modules/untracked/LICENSE", AuditFixture.Mit);
        foreach (var container in new[] { "leaf@1.0.0/node_modules", "node_modules" })
        {
            var bin = fixture.Audit.Full("web/node_modules/.pnpm/" + container + "/.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
            File.CreateSymbolicLink(bin, fixture.Audit.Full("missing-bin"));
        }
        fixture.Save();
        fixture.Audit.Check().Errors.ShouldBeEmpty();
    }

    private static void ScopeLeaf(PnpmRepairFixture fixture)
    {
        fixture.Rewrite("leaf", metadata => metadata["name"] = "@scope/leaf");
        PnpmRepairFixture.Rename(fixture.Packages, "leaf@1.0.0", "@scope/leaf@1.0.0");
        PnpmRepairFixture.Rename(fixture.Snapshots, "leaf@1.0.0", "@scope/leaf@1.0.0");
        fixture.Rewrite("consumer", metadata =>
            PnpmRepairFixture.Rename(metadata["dependencies"]!.AsObject(), "leaf", "@scope/leaf"));
        PnpmRepairFixture.Rename(fixture.Snapshots[PnpmRepairFixture.Consumer]!["dependencies"]!.AsObject(), "leaf", "@scope/leaf");
    }
}
