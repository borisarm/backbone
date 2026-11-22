using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Backbone.Service.Os;

/// <summary>Lectura de los nombres de una enumeración por handle (<c>GetFileInformationByHandleEx</c>, <c>FileFullDirectoryInfo</c>).</summary>
internal static class DirectoryListing
{
    private const int NameOffset = 68;

    /// <summary>
    /// Nombres de un búfer de <c>FILE_FULL_DIR_INFO</c>: <c>NextEntryOffset</c> (0), <c>FileNameLength</c> en bytes (60) y
    /// nombre (68). Comprueba que cada entrada, su nombre y el desplazamiento a la siguiente caben en el búfer; si no,
    /// <see cref="IOException"/>.
    /// </summary>
    public static List<string> ReadNames(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var names = new List<string>();
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > buffer.Length - NameOffset)
            {
                throw Malformed();
            }

            var span = buffer.AsSpan(offset);
            var next = BinaryPrimitives.ReadUInt32LittleEndian(span);
            var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(span[60..]);
            if (nameBytes % 2 != 0 || nameBytes > (uint)(span.Length - NameOffset))
            {
                throw Malformed();
            }

            names.Add(new string(MemoryMarshal.Cast<byte, char>(span.Slice(NameOffset, (int)nameBytes))));
            if (next == 0)
            {
                return names;
            }

            if (next < NameOffset || next > (uint)span.Length || next % 8 != 0)
            {
                throw Malformed();
            }

            offset += (int)next;
        }
    }

    private static IOException Malformed() => new("The directory listing returned by the system is malformed.");
}
