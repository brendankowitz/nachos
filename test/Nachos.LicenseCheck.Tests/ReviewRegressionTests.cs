using System.IO.Compression;
using System.Text.Json;
using Shouldly;

namespace Nachos.LicenseCheck.Tests;

public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData("", "\n", false, false, "[yyyy]")]
    [InlineData("   ", "\n", false, false, "[yyyy]")]
    [InlineData("\t", "\n", false, false, "[yyyy]")]
    [InlineData("   ", "\r\n", false, false, "[yyyy]")]
    [InlineData("\t", "\r\n", false, false, "[yyyy]")]
    [InlineData(" \t ", "\n", true, false, "[yyyy]")]
    [InlineData("   ", "\n", false, true, "[yyyy]")]
    [InlineData("\t ", "\r\n", true, true, "[yyyy]")]
    [InlineData("\t", "\n", false, false, "2026")]
    public void E1_CompleteApacheWithCopyrightIndentation_Passes(
        string indentation, string newline, bool uppercase, bool bom, string year)
    {
        var text = CompleteLicense("Apache-2.0")
            .Replace("Copyright [yyyy]", indentation + "Copyright " + year, StringComparison.Ordinal)
            .ReplaceLineEndings(newline);
        text = uppercase ? text.ToUpperInvariant() : text;
        text = bom ? "\uFEFF" + text : text;
        using var fixture = new AuditFixture();
        fixture.Nuget("e1-apache", "Apache-2.0", text);
        fixture.Publish("e1-apache");

        var report = fixture.Check();

        report.Errors.ShouldBeEmpty();
        var decision = report.Packages.Single(package => package.Package == "e1-apache");
        decision.SelectedLicense.ShouldBe("Apache-2.0");
        decision.Tier.ShouldBe("distributed");
    }

    [Theory]
    [InlineData("grant", "")]
    [InlineData("grant", " \t ")]
    [InlineData("disclaimer", "")]
    [InlineData("disclaimer", " \t ")]
    [InlineData("appended-restriction", "")]
    [InlineData("appended-restriction", " \t ")]
    [InlineData("holder-restriction", "")]
    [InlineData("holder-restriction", " \t ")]
    [InlineData("unknown-holder", "")]
    [InlineData("unknown-holder", " \t ")]
    [InlineData("unknown-component", "")]
    [InlineData("unknown-component", " \t ")]
    public void E1_ChangedCompleteApacheText_FailsClosed(string mutation, string indentation)
    {
        var complete = CompleteLicense("Apache-2.0");
        var text = mutation switch
        {
            "grant" => RemoveApacheSection(complete, "2. Grant of Copyright License.", "3. Grant of Patent License."),
            "disclaimer" => RemoveApacheSection(complete, "7. Disclaimer of Warranty.", "8. Limitation of Liability."),
            "appended-restriction" => complete + "\nCommercial redistribution is prohibited.",
            "holder-restriction" => complete.Replace("[name of copyright owner]",
                "[name of copyright owner] Commercial redistribution is prohibited.", StringComparison.Ordinal),
            "unknown-holder" => complete.Replace("[name of copyright owner]", "Unknown Owner", StringComparison.Ordinal),
            "unknown-component" => complete + "\nBundled component: LicenseRef-Unreviewed-Component.",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        text = text.Replace("Copyright [yyyy]", indentation + "Copyright [yyyy]", StringComparison.Ordinal);
        using var fixture = new AuditFixture();
        fixture.Nuget("e1-invalid", "Apache-2.0", text);

        var report = fixture.Check();

        report.Errors.Count.ShouldBe(1);
        report.Errors.ShouldContain(error => error.Contains("e1-invalid", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Package == "e1-invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E1_IndentedApacheCannotHideGplGrantOrAndMetadata(bool prohibitedMetadata)
    {
        var text = CompleteLicense("Apache-2.0")
            .Replace("Copyright [yyyy]", "\tCopyright [yyyy]", StringComparison.Ordinal);
        if (!prohibitedMetadata)
        {
            text += "\nThis program is free software: you can redistribute it and/or modify it under the terms "
                + "of the GNU General Public License as published by the Free Software Foundation, version 3.";
        }
        using var fixture = new AuditFixture();
        fixture.Nuget("e1-prohibited", prohibitedMetadata ? "Apache-2.0 AND LGPL-3.0-only" : "Apache-2.0", text);

        var report = fixture.Check();

        report.Errors.ShouldContain(error => error.Contains("prohibited GPL/AGPL/LGPL/SSPL", StringComparison.Ordinal));
        report.Packages.ShouldNotContain(package => package.Package == "e1-prohibited");
    }

    private static string RemoveApacheSection(string text, string heading, string nextHeading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        var end = text.IndexOf(nextHeading, start, StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        end.ShouldBeGreaterThan(start);
        return text.Remove(start, end - start);
    }

    [Theory]
    [InlineData("Commercial redistribution prohibited")]
    [InlineData("For educational purposes only")]
    [InlineData("Military deployment disallowed")]
    public void CI1_OperativeTermsInCopyrightField_AreNotNormalizedAway(string terms)
    {
        using var fixture = new AuditFixture();
        fixture.Npm("modified", "MIT", CompleteLicense("MIT").Replace("<year> <copyright holders>",
            $"2026 Example Company {terms}", StringComparison.Ordinal));
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI1_UnrecognizedCopyrightAttribution_RequiresReviewRatherThanDeletion()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("modified", "MIT", CompleteLicense("MIT").Replace("<copyright holders>", "Unknown Owner", StringComparison.Ordinal));
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("\nAdditional condition: Commercial use of the Software is forbidden.")]
    [InlineData("\nUse requires a separate paid license.")]
    public void CI1_AddedOperativeTerms_Fail(string addition)
    {
        using var fixture = new AuditFixture();
        fixture.Npm("modified", "MIT", CompleteLicense("MIT") + addition);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("MIT")]
    [InlineData("Apache-2.0")]
    [InlineData("EPL-2.0")]
    [InlineData("ISC")]
    public void CI1_TruncatedCompleteLicense_Fails(string license)
    {
        using var fixture = new AuditFixture();
        var text = CompleteLicense(license);
        fixture.Npm("truncated", license, text[..(text.Length * 3 / 4)]);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI1_SixMatchingFragments_AreNotALicense()
    {
        using var fixture = new AuditFixture();
        fixture.Npm("fragments", "MIT", """
            permission is hereby granted, free of charge
            to deal in the software without restriction
            the above copyright notice and this permission notice shall be included
            the software is provided
            without warranty of any kind
            in no event shall
            """);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("MIT")]
    [InlineData("MIT-0")]
    [InlineData("Apache-2.0")]
    [InlineData("BSD-2-Clause")]
    [InlineData("BSD-3-Clause")]
    [InlineData("0BSD")]
    [InlineData("ISC")]
    [InlineData("MS-PL")]
    [InlineData("Unlicense")]
    [InlineData("CC0-1.0")]
    [InlineData("BlueOak-1.0.0")]
    [InlineData("Zlib")]
    [InlineData("PSF-2.0")]
    [InlineData("Python-2.0")]
    public void CI1_CompletePermissiveText_Passes(string license)
    {
        using var fixture = new AuditFixture();
        fixture.Npm("complete", license, CompleteLicense(license));
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("License: GPL-3.0-only\n")]
    [InlineData("License: Example proprietary terms\n")]
    [InlineData("Classifier: License :: OSI Approved :: GNU General Public License v3 (GPLv3)\n")]
    [InlineData("Classifier: License :: Other/Proprietary License\n")]
    public void CI2_LegacyPythonDeclarations_AreNotIgnored(string declaration)
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        ReplaceMetadata(fixture, "client", "client-1.0.0.dist-info/METADATA",
            $"Metadata-Version: 2.1\nName: client\nVersion: 1.0.0\n{declaration}\n");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI2_ConflictingModernAndLegacyDeclarations_Fail()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit, "License: Apache-2.0\n");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI3_VendoredLicense_CannotLicensePythonPackage()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", null);
        ReplaceMetadata(fixture, "client", "client-1.0.0.dist-info/METADATA",
            "Metadata-Version: 2.1\nName: client\nVersion: 1.0.0\n\n");
        AddWheelEntry(fixture, "client", "client/_vendor/foo/LICENSE", AuditFixture.Mit);
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI5_ConflictingWheelMetadataDirectories_Fail()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        AddWheelEntry(fixture, "client", "other-2.0.0.dist-info/METADATA",
            "Metadata-Version: 2.4\nName: other\nVersion: 2.0.0\nLicense-Expression: GPL-3.0-only\n\n");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI6_PrimaryMetadata_DoesNotDescribeSupplementalObligations()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("composite", "MIT", AuditFixture.Mit, notice: CompleteLicense("Apache-2.0"));
        var report = fixture.Check();
        report.Errors.ShouldBeEmpty();
        report.Packages.Single(package => package.Package == "composite").SelectedLicense.ShouldBe("Apache-2.0 AND MIT");
    }

    [Fact]
    public void CI2_AgreeingLegacyLicenseAndClassifier_Pass()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", AuditFixture.Mit);
        ReplaceMetadata(fixture, "client", "client-1.0.0.dist-info/METADATA",
            "Metadata-Version: 2.1\nName: client\nVersion: 1.0.0\nLicense: MIT\nClassifier: License :: OSI Approved :: MIT License\n\n");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("other", "MIT")]
    [InlineData("client", "GPL-3.0-only")]
    public void CI5_ConflictingSdistMetadata_Fails(string name, string license)
    {
        using var fixture = new AuditFixture();
        fixture.PythonSdist("client", "MIT", AuditFixture.Mit, extraMetadata:
            $"Metadata-Version: 2.4\nName: {name}\nVersion: 1.0.0\nLicense-Expression: {license}\nLicense-File: LICENSE\n\n");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void CI5_AgreeingRepeatedSdistMetadata_Passes()
    {
        using var fixture = new AuditFixture();
        fixture.PythonSdist("client", "MIT", AuditFixture.Mit, extraMetadata:
            "Metadata-Version: 2.4\nName: client\nVersion: 1.0.0\nLicense-Expression: MIT\nLicense-File: LICENSE\n\n");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void CI3_ReviewedExactVersionEvidence_StillSuppliesMissingPrimary()
    {
        using var fixture = new AuditFixture();
        fixture.Python("client", "MIT", null);
        ReplaceMetadata(fixture, "client", "client-1.0.0.dist-info/METADATA",
            "Metadata-Version: 2.1\nName: client\nVersion: 1.0.0\n\n");
        AddWheelEntry(fixture, "client", "client/_vendor/foo/LICENSE", AuditFixture.Mit);
        fixture.Override("python", "client", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void CI6_SupplementalProhibitedText_RemainsDisallowed()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("composite", "MIT", AuditFixture.Mit, notice: AuditFixture.Gpl);
        fixture.Check().Errors.ShouldContain(error => error.Contains("prohibited", StringComparison.Ordinal));
    }

    [Fact]
    public void CI6_PrimaryOverride_CannotDiscardUnknownSupplement()
    {
        using var fixture = new AuditFixture();
        fixture.Nuget("composite", "MIT", AuditFixture.Mit, notice: "Unidentified custom supplemental terms.");
        fixture.Override("nuget", "composite", "1.0.0", "MIT");
        fixture.Check().Errors.ShouldNotBeEmpty();
    }

    internal static string CompleteLicense(string id) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "complete-licenses.json")))![id];

    internal static void ReplaceMetadata(AuditFixture fixture, string name, string entryName, string text)
    {
        using var zip = ZipFile.Open(fixture.Full($"python-archives/{name.Replace('-', '_')}-1.0.0-py3-none-any.whl"), ZipArchiveMode.Update);
        zip.GetEntry(entryName)?.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
        writer.Write(text);
    }

    internal static void AddWheelEntry(AuditFixture fixture, string name, string entryName, string text) =>
        ReplaceMetadata(fixture, name, entryName, text);
}
