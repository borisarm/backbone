using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Backbone.Service.Os;

/// <summary>Lectura de los nombres de una enumeración por handle (<c>GetFileInformationByHandleEx</c>, <c>FileFullDirectoryInfo</c>).</summary>
internal static class DirectoryListing
{
    /// <summary>
    /// Nombres de un búfer de <c>FILE_FULL_DIR_INFO</c>: <c>NextEntryOffset</c> (0), <c>FileNameLength</c> en bytes (60) y
    /// nombre (68).
    /// </summary>
    public static List<string> ReadNames(byte[] buffer)
    {
        var names = new List<string>();
        var offset = 0;
        while (true)
        {
            var span = buffer.AsSpan(offset);
            var next = BinaryPrimitives.ReadUInt32LittleEndian(span);
            var nameBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[60..]);
            names.Add(new string(MemoryMarshal.Cast<byte, char>(span.Slice(68, nameBytes))));
            if (next == 0)
            {
                return names;
            }

            offset += (int)next;
        }
    }
}
