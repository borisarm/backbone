using System.Security.Cryptography;
using Backbone.Core.Repository;
using Backbone.Core.Storage;

namespace Backbone.Core.Tests.Repository;

/// <summary>Nombres de objetos (§6.1): SHA-256 en hexadecimal minúsculo de 64 caracteres.</summary>
[Trait("Story", "HT-04")]
public sealed class ContentHashTests
{
    [Fact]
    public void HT04_1_Hash_is_lowercase_hex_and_names_the_object_path()
    {
        var hash = ContentHash.FromBytes(SHA256.HashData("abc"u8));

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash.ToString());
        Assert.Equal("ba", hash.Prefix);
        Assert.Equal("objects/ba/ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", RepositoryLayout.ObjectPath(hash));
        Assert.Equal("objects/ba", RepositoryLayout.ObjectDirectory(hash));
    }

    [Fact]
    public void HT04_1_Parse_accepts_only_canonical_hashes()
    {
        var valid = new string('0', 63) + "f";
        Assert.Equal(valid, ContentHash.Parse(valid).ToString());

        Assert.False(ContentHash.TryParse(null, out _));
        Assert.False(ContentHash.TryParse(new string('0', 63), out _));
        Assert.False(ContentHash.TryParse(new string('0', 63) + "F", out _));
        Assert.False(ContentHash.TryParse(new string('0', 63) + "g", out _));
        Assert.Throws<FormatException>(() => ContentHash.Parse("x"));
        Assert.Throws<ArgumentException>(() => ContentHash.FromBytes(new byte[31]));
    }

    [Fact]
    public void HT04_1_Equality_and_default()
    {
        var a = ContentHash.FromBytes(new byte[32]);
        var b = ContentHash.Parse(new string('0', 64));

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals("x"));
        Assert.NotEqual(default, a);
        Assert.Equal(0, default(ContentHash).GetHashCode());
        Assert.Throws<InvalidOperationException>(() => default(ContentHash).ToString());
    }

    [Fact]
    public void HT04_1_Repository_paths_are_combined_and_validated()
    {
        Assert.Equal("snapshots/20261115T020000Z-a1b2.json", RepositoryLayout.SnapshotPath("20261115T020000Z-a1b2"));
        Assert.Matches("^tmp/[0-9a-f]{32}\\.tmp$", RepositoryLayout.NewTempPath());
        Assert.Throws<ArgumentException>(() => RepositoryPath.Combine("tmp", ".."));
        Assert.Throws<ArgumentException>(() => RepositoryPath.Combine("tmp", "a/../.."));
        Assert.Empty(RepositoryPath.Validate("", allowRoot: true));
        Assert.Throws<ArgumentException>(() => RepositoryPath.Validate("", allowRoot: false));
    }

    [Fact]
    public void HT04_1_Repository_exception_constructors_default_to_corrupt()
    {
        Assert.Equal(RepositoryErrorKind.Corrupt, new RepositoryException().Kind);
        Assert.Equal(RepositoryErrorKind.Corrupt, new RepositoryException("m").Kind);
        var inner = new IOException();
        var ex = new RepositoryException("m", inner);
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Same(inner, ex.InnerException);
    }
}
