using Backbone.Service.Hosting;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// §8.3 "Carpetas protegidas" (ADR-16, M-B): clasificación de la ruta final de un origen frente a la carpeta de datos y las
/// carpetas apartadas, y cálculo de la lista (como mucho 100, fallo cerrado).
/// </summary>
[Trait("Story", "HU-07")]
public sealed class ProtectedFoldersTests
{
    private const string ProgramData = @"C:\ProgramData";

    private static ProtectedFolderSet Set(params string[] quarantined) => new(ProgramData, "Backbone", quarantined, IsComplete: true);

    [Theory]
    [InlineData(@"C:\ProgramData\Backbone", @"C:\ProgramData\Backbone")]
    [InlineData(@"C:\ProgramData\Backbone\credentials", @"C:\ProgramData\Backbone")]
    [InlineData(@"c:\programdata\BACKBONE\config", @"C:\ProgramData\BACKBONE")]
    [InlineData(@"\\?\C:\ProgramData\Backbone\logs", @"C:\ProgramData\Backbone")]
    [InlineData(@"C:\ProgramData\Backbone.untrusted-0123456789abcdef0123456789abcdef", @"C:\ProgramData\Backbone.untrusted-0123456789abcdef0123456789abcdef")]
    [InlineData(@"C:\ProgramData\backbone.UNTRUSTED-x\credentials", @"C:\ProgramData\backbone.UNTRUSTED-x")]
    public void A_source_that_is_or_is_inside_a_protected_folder_is_not_allowed(string source, string protectedPath)
    {
        var result = Set().Classify(source);

        Assert.Equal(SourceProtectionKind.InsideProtectedFolder, result.Kind);
        Assert.True(result.IsNotAllowed);
        Assert.Equal(protectedPath, result.ProtectedPath, ignoreCase: true);
        Assert.Empty(result.MandatoryExclusions);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\BackboneOtra")]
    [InlineData(@"C:\ProgramData\Backbone.untrusted")]
    [InlineData(@"C:\ProgramData\Microsoft")]
    [InlineData(@"C:\Users\ana\Documents")]
    [InlineData(@"D:\ProgramData\Backbone")]
    [InlineData(@"C:\Users\ana\ProgramData\Backbone")]
    public void Unrelated_sources_are_allowed_without_exclusions(string source)
    {
        var result = Set("Backbone.untrusted-a").Classify(source);

        Assert.Equal(SourceProtectionKind.None, result.Kind);
        Assert.False(result.IsNotAllowed);
        Assert.Empty(result.MandatoryExclusions);
    }

    [Fact]
    public void A_drive_root_contains_the_data_folder_and_the_real_quarantined_folders_as_relative_exclusions()
    {
        var result = Set("Backbone.untrusted-a", "Backbone.untrusted-b").Classify(@"C:\");

        Assert.Equal(SourceProtectionKind.ContainsProtectedFolders, result.Kind);
        Assert.False(result.IsNotAllowed);
        Assert.Equal(@"C:\ProgramData\Backbone", result.ProtectedPath);
        Assert.Equal([@"ProgramData\Backbone", @"ProgramData\Backbone.untrusted-a", @"ProgramData\Backbone.untrusted-b"], result.MandatoryExclusions);
    }

    [Fact]
    public void ProgramData_itself_contains_the_protected_folders_directly()
    {
        var result = Set("Backbone.untrusted-a").Classify(@"c:\programdata");

        Assert.Equal(SourceProtectionKind.ContainsProtectedFolders, result.Kind);
        Assert.Equal(["Backbone", "Backbone.untrusted-a"], result.MandatoryExclusions);
    }

    [Fact]
    public void With_an_incomplete_list_a_source_containing_ProgramData_fails_closed()
    {
        var set = new ProtectedFolderSet(ProgramData, "Backbone", [], IsComplete: false);

        var result = set.Classify(@"C:\");

        Assert.Equal(SourceProtectionKind.ProtectedFoldersUnknown, result.Kind);
        Assert.True(result.IsNotAllowed);
        Assert.Equal(ProgramData, result.ProtectedPath);
    }

    [Fact]
    public void With_an_incomplete_list_other_sources_are_not_affected_and_inside_still_uses_the_location()
    {
        var set = new ProtectedFolderSet(ProgramData, "Backbone", [], IsComplete: false);

        Assert.Equal(SourceProtectionKind.None, set.Classify(@"C:\Users\ana").Kind);
        Assert.Equal(SourceProtectionKind.InsideProtectedFolder, set.Classify(@"C:\ProgramData\Backbone.untrusted-zz\x").Kind);
    }

    [Fact]
    public void The_resolver_uses_the_final_path_of_the_parent_and_the_quarantine_prefix_of_the_data_folder()
    {
        var fileSystem = new FakeLocalFileSystemInspector().MapFinalDirectory(@"C:\Datos", @"E:\Real\Datos");
        fileSystem.QuarantinedFolders.AddRange(["Backbone.untrusted-1", "Otra.untrusted-2"]);
        var resolver = new ProtectedFolderResolver(new ServicePaths(@"C:\Datos\Backbone"), fileSystem, NullLogger<ProtectedFolderResolver>.Instance);

        var set = resolver.Resolve(TestIo.Guard());

        Assert.Equal(@"E:\Real\Datos", set.ProgramDataPath);
        Assert.Equal("Backbone", set.DataFolderName);
        Assert.Equal(["Backbone.untrusted-1"], set.QuarantinedFolderNames);
        Assert.True(set.IsComplete);
    }

    [Fact]
    public void Up_to_100_quarantined_folders_are_listed()
    {
        var fileSystem = new FakeLocalFileSystemInspector();
        fileSystem.QuarantinedFolders.AddRange(Enumerable.Range(0, ProtectedFolderResolver.MaxQuarantinedFolders).Select(i => $"Backbone.untrusted-{i:D3}"));

        var set = NewResolver(fileSystem).Resolve(TestIo.Guard());

        Assert.True(set.IsComplete);
        Assert.Equal(100, set.QuarantinedFolderNames.Count);
        Assert.Equal(101, set.Classify(@"C:\").MandatoryExclusions.Count);
    }

    [Fact]
    public void More_than_100_quarantined_folders_fail_closed_and_log_a_warning_with_the_count()
    {
        var fileSystem = new FakeLocalFileSystemInspector();
        fileSystem.MapFinalDirectory(@"C:\ProgramData", @"C:\ProgramData");
        fileSystem.QuarantinedFolders.AddRange(Enumerable.Range(0, 150).Select(i => $"Backbone.untrusted-{i:D3}"));
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));

        var set = new ProtectedFolderResolver(new ServicePaths(@"C:\ProgramData\Backbone"), fileSystem, factory.CreateLogger<ProtectedFolderResolver>()).Resolve(TestIo.Guard());

        Assert.False(set.IsComplete);
        Assert.Empty(set.QuarantinedFolderNames);
        Assert.Equal(SourceProtectionKind.ProtectedFoldersUnknown, set.Classify(@"C:\").Kind);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("100", StringComparison.Ordinal));

        // MEDIA-1: deja de abrir entradas en cuanto encuentra la 101.
        Assert.Equal(101, fileSystem.OpenedEntries);
    }

    [Fact]
    public void ProgramData_is_resolved_with_the_controlled_resolution_and_fails_closed_if_it_cannot_be_resolved()
    {
        var fileSystem = new FakeLocalFileSystemInspector().AddStatus(@"C:\ProgramData", LocalFolderStatus.NotLocal);

        var set = new ProtectedFolderResolver(new ServicePaths(@"C:\ProgramData\Backbone"), fileSystem, NullLogger<ProtectedFolderResolver>.Instance).Resolve(TestIo.Guard());

        Assert.Equal([@"C:\ProgramData"], fileSystem.Resolved);
        Assert.False(set.IsComplete);
        Assert.Equal(0, fileSystem.EnumerationCount);
    }

    [Fact]
    public void An_enumeration_failure_fails_closed()
    {
        var fileSystem = new FakeLocalFileSystemInspector { EnumerationFailure = new IOException("denied") };

        var set = NewResolver(fileSystem).Resolve(TestIo.Guard());

        Assert.False(set.IsComplete);
        Assert.True(set.Classify(@"C:\ProgramData").IsNotAllowed);
    }

    [Fact]
    public void The_list_is_computed_again_on_every_call()
    {
        var fileSystem = new FakeLocalFileSystemInspector();
        var resolver = NewResolver(fileSystem);

        Assert.Empty(resolver.Resolve(TestIo.Guard()).QuarantinedFolderNames);
        fileSystem.QuarantinedFolders.Add("Backbone.untrusted-nueva");

        Assert.Equal(["Backbone.untrusted-nueva"], resolver.Resolve(TestIo.Guard()).QuarantinedFolderNames);
        Assert.Equal(2, fileSystem.EnumerationCount);
    }

    private static ProtectedFolderResolver NewResolver(FakeLocalFileSystemInspector fileSystem) =>
        new(new ServicePaths(@"C:\ProgramData\Backbone"), fileSystem.MapFinalDirectory(@"C:\ProgramData", @"C:\ProgramData"), NullLogger<ProtectedFolderResolver>.Instance);
}
