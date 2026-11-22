using System.Buffers.Binary;
using System.Text;
using Backbone.Service.Os;
using Backbone.Service.Sources;

namespace Backbone.Service.Tests.Sources;

/// <summary>
/// Lectura de los búferes de enumeración por handle (<c>FILE_ID_BOTH_DIR_INFO</c> en <c>SourceFileSystem</c>,
/// <c>FILE_FULL_DIR_INFO</c> en la búsqueda de carpetas apartadas): un búfer mal formado es un error, nunca una lectura fuera
/// de los límites.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class DirectoryBufferParsingTests
{
    private const int BothNameOffset = 104;
    private const int FullNameOffset = 68;

    [Fact]
    public void A_well_formed_id_both_buffer_yields_every_entry_with_its_tag_in_EaSize()
    {
        var buffer = Build(BothNameOffset, ("a.txt", 0x20u, 0u), ("enlace", 0x410u, 0xA000_0003u));
        var entries = new List<RawDirectoryEntry>();

        Win32SourceHandle.ParseDirectoryBuffer(buffer, entries);

        Assert.Equal(["a.txt", "enlace"], entries.Select(e => e.Name));
        Assert.Equal(0xA000_0003u, entries[1].EaSize);
        Assert.Equal((FileAttributes)0x410, entries[1].Attributes);
    }

    [Fact]
    public void A_well_formed_full_buffer_yields_every_name() =>
        Assert.Equal(["Backbone.untrusted-1", "otra"], DirectoryListing.ReadNames(Build(FullNameOffset, ("Backbone.untrusted-1", 0x10u, 0u), ("otra", 0x10u, 0u))));

    [Theory]
    [InlineData("next-beyond-buffer")]
    [InlineData("next-not-aligned")]
    [InlineData("next-inside-header")]
    [InlineData("name-beyond-buffer")]
    [InlineData("odd-name-length")]
    [InlineData("truncated-header")]
    public void A_malformed_buffer_is_an_error_in_both_parsers(string defect)
    {
        var both = Corrupt(Build(BothNameOffset, ("a.txt", 0x20u, 0u), ("b.txt", 0x20u, 0u)), BothNameOffset, defect);
        var full = Corrupt(Build(FullNameOffset, ("a.txt", 0x20u, 0u), ("b.txt", 0x20u, 0u)), FullNameOffset, defect);

        Assert.ThrowsAny<IOException>(() => Win32SourceHandle.ParseDirectoryBuffer(both, []));
        Assert.ThrowsAny<IOException>(() => DirectoryListing.ReadNames(full));
    }

    private static byte[] Corrupt(byte[] buffer, int nameOffset, string defect)
    {
        switch (defect)
        {
            case "next-beyond-buffer":
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)buffer.Length + 8);
                break;
            case "next-not-aligned":
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, BinaryPrimitives.ReadUInt32LittleEndian(buffer) + 2);
                break;
            case "next-inside-header":
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, 8);
                break;
            case "name-beyond-buffer":
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60), 0x7FFF_FFF0);
                break;
            case "odd-name-length":
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60), 3);
                break;
            case "truncated-header":
                return buffer[..(nameOffset - 1)];
        }

        return buffer;
    }

    /// <summary>Entradas encadenadas con <c>NextEntryOffset</c> alineado a 8; atributos en 56, longitud en 60, EaSize en 64.</summary>
    private static byte[] Build(int nameOffset, params (string Name, uint Attributes, uint EaSize)[] entries)
    {
        var chunks = new List<byte[]>();
        foreach (var (name, attributes, eaSize) in entries)
        {
            var nameBytes = Encoding.Unicode.GetBytes(name);
            var size = (nameOffset + nameBytes.Length + 7) & ~7;
            var chunk = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(56), attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(60), (uint)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(64), eaSize);
            nameBytes.CopyTo(chunk, nameOffset);
            chunks.Add(chunk);
        }

        for (var i = 0; i < chunks.Count - 1; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(chunks[i], (uint)chunks[i].Length);
        }

        return [.. chunks.SelectMany(c => c)];
    }
}
