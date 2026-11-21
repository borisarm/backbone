using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Sources;

namespace Backbone.Service.Tests.Sources;

/// <summary>§8.2 "Rutas y raíces": deducción de la ruta relativa y forma de las raíces (ADR-18).</summary>
[Trait("Story", "HU-08")]
public sealed class SourceRootsTests
{
    private static SourceRoots Roots(params string[] readPaths) =>
        new([.. readPaths.Select((p, i) => new BackupSource("o" + i, p, p))]);

    [Fact]
    public void The_root_and_its_children_resolve_to_their_relative_path()
    {
        var roots = Roots(@"C:\Users\ana");

        var root = roots.Resolve(@"C:\Users\ana");
        var child = roots.Resolve(@"C:\Users\ana\Documentos\a.txt");

        Assert.True(root.IsRoot);
        Assert.Equal(["Documentos", "a.txt"], child.Relative);
        Assert.Equal(@"\\?\C:\Users\ana\Documentos\a.txt", child.Root.OpenPathFor(child.Relative));
        Assert.Equal(@"\\?\C:\Users\ana", root.Root.ExpectedRootFinal);
        Assert.Equal(SourceRootKind.DriveLetter, root.Root.Kind);
    }

    [Theory]
    [InlineData(@"C:\Users\otro\a.txt")]
    [InlineData(@"C:\Users\anabel")]
    [InlineData(@"C:\Users\ana\..\otro")]
    [InlineData(@"C:\Users\ana\.\a")]
    [InlineData(@"C:\Users\ana\a\\b")]
    [InlineData(@"C:\Users\ana\")]
    [InlineData(@"C:\Users\ana\a.txt:secreto")]
    [InlineData(@"C:\Users\ana\a/b")]
    [InlineData(@"D:\Users\ana\a")]
    [InlineData("")]
    public void Paths_outside_the_sources_or_with_invalid_segments_are_rejected_with_PathInvalid(string path)
    {
        var roots = Roots(@"C:\Users\ana");

        var ex = Assert.Throws<SourceAccessException>(() => roots.Resolve(path));

        Assert.Equal(RunIssueCode.PathInvalid, ex.Code);
    }

    [Fact]
    public void A_volume_root_composes_children_without_a_double_separator_and_opens_with_the_trailing_backslash()
    {
        var roots = Roots(@"C:\");
        var root = roots.Resolve(@"C:\");

        Assert.Equal(@"C:\Windows", SourceRoots.ChildPath(root, "Windows"));
        Assert.Equal(@"\\?\C:\", root.Root.OpenRoot);
        Assert.Equal(["Windows"], roots.Resolve(@"C:\Windows").Relative);
    }

    [Fact]
    public void A_shadow_copy_root_is_compared_with_its_nt_device_path()
    {
        var roots = Roots(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\Users\ana");
        var root = roots.Resolve(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\Users\ana");

        Assert.Equal(SourceRootKind.Device, root.Root.Kind);
        Assert.Equal(@"\Device\HarddiskVolumeShadowCopy7\Users\ana", root.Root.ExpectedRootFinal);
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\Users\ana\x", root.Root.OpenPathFor(["x"]));
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\", Roots(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7").Roots[0].OpenRoot);
    }

    [Fact]
    public void A_volume_guid_root_is_compared_with_its_guid_path()
    {
        const string path = @"\\?\Volume{6b29fc40-ca47-1067-b31d-00dd010662da}\Datos";
        var root = Roots(path).Resolve(path);

        Assert.Equal(SourceRootKind.VolumeGuid, root.Root.Kind);
        Assert.Equal(path, root.Root.ExpectedRootFinal);
    }

    [Theory]
    [InlineData(@"\\nas\recurso\carpeta")]
    [InlineData(@"\\?\UNC\nas\recurso")]
    [InlineData(@"relativo\carpeta")]
    [InlineData(@"C:\Users\..\Windows")]
    public void Read_paths_that_are_not_local_volume_paths_are_a_programming_error(string readPath) =>
        Assert.Throws<ArgumentException>(() => Roots(readPath));

    [Fact]
    public void The_longest_matching_source_wins_when_one_is_inside_another()
    {
        var roots = Roots(@"C:\Users", @"C:\Users\ana");

        var resolved = roots.Resolve(@"C:\Users\ana\a.txt");

        Assert.Equal(@"C:\Users\ana", resolved.Root.ReadPath);
        Assert.Equal(["a.txt"], resolved.Relative);
    }

    [Fact]
    public void Mandatory_exclusions_match_by_whole_segments_without_case()
    {
        var source = new BackupSource("c", @"C:\", @"C:\") { MandatoryExclusions = [@"ProgramData\Backbone", "ProgramData/Backbone.untrusted-1"] };
        var root = new SourceRoots([source]).Roots[0];

        Assert.True(root.IsExcluded(["programdata", "BACKBONE"]));
        Assert.True(root.IsExcluded(["ProgramData", "Backbone", "credentials", "x.bin"]));
        Assert.True(root.IsExcluded(["ProgramData", "Backbone.untrusted-1"]));
        Assert.False(root.IsExcluded(["ProgramData", "BackboneX"]));
        Assert.False(root.IsExcluded(["ProgramData"]));
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\a\b", @"\device\harddiskvolume3\A\B\", true)]
    [InlineData(@"\Device\HarddiskVolume3\a\b", @"\Device\HarddiskVolume3\a\bc", false)]
    [InlineData(@"\Device\HarddiskVolume3\a", @"\Device\HarddiskVolume3\a\b", false)]
    public void Final_paths_are_compared_by_whole_segments_without_case(string left, string right, bool equal) =>
        Assert.Equal(equal, SourceRoots.SegmentsEqual(left, right));
}
