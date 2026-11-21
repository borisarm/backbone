namespace Backbone.Core.Engine;

/// <summary>
/// Atributos y etiquetas de reanálisis de origen que el motor interpreta, y la clasificación de cada elemento antes de abrir
/// nada (RF-06, R4, ADR-19, §8.2 "Clasificación por etiqueta").
/// </summary>
public static class SourceAttributes
{
    /// <summary>
    /// <c>FILE_ATTRIBUTE_RECALL_ON_OPEN</c> (0x00040000): el elemento no tiene representación local; abrirlo lo trae de un
    /// almacén remoto. Solo aparece en la enumeración de carpetas. <see cref="FileAttributes"/> no le da nombre.
    /// </summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>
    /// <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c> (0x00400000): el contenido no está entero en local (p. ej. un archivo de
    /// OneDrive "solo en la nube"); leerlo lo descarga.
    /// </summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>
    /// Clasifica un elemento por sus atributos y su etiqueta de reanálisis (ADR-19, §8.2 "Clasificación por etiqueta"), en
    /// el orden de la especificación: <see cref="SourceEntryKind.Link"/>, <see cref="SourceEntryKind.CloudOnly"/>,
    /// <see cref="SourceEntryKind.Unreadable"/> y, por último, <see cref="SourceEntryKind.Content"/> o
    /// <see cref="SourceEntryKind.Normal"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Sin <see cref="FileAttributes.ReparsePoint"/>, la etiqueta se ignora (vale 0).</description></item>
    /// <item><description><b>Enlace</b>: etiqueta con el bit sustituto de nombre (<see cref="ReparseTags.NameSurrogateBit"/>), tenga los atributos que tenga.</description></item>
    /// <item><description>
    /// <b>Solo en la nube</b>: <see cref="RecallOnOpen"/> o <see cref="RecallOnDataAccess"/> (solo los pone el modo
    /// núcleo), o <see cref="FileAttributes.Offline"/> con etiqueta de nube o de HSM. <c>OFFLINE</c> sin etiqueta no
    /// significa nada (lo puede poner cualquier usuario); con otra etiqueta manda la etiqueta. Vale igual para carpetas
    /// (carpetas de nube no pobladas). El estado <c>PARTIAL</c> del marcador no se ve en la enumeración: lo comprueba la
    /// implementación sobre el handle y lanza <see cref="Backbone.Contracts.RunIssueCode.CloudOnlySkipped"/>.
    /// </description></item>
    /// <item><description>
    /// <b>No legible</b>: cualquier otra etiqueta fuera de la lista blanca, y un punto de reanálisis con etiqueta 0 (una
    /// implementación que no la rellena), que nunca se trata como normal.
    /// </description></item>
    /// <item><description><b>Contenido</b>: lista blanca (<see cref="ReparseTags.IsCloud"/>, <see cref="ReparseTags.Wof"/>, <see cref="ReparseTags.Dedup"/>).</description></item>
    /// </list>
    /// </remarks>
    /// <param name="attributes">Atributos tal como los da el sistema (<see cref="SourceEntry.Attributes"/>).</param>
    /// <param name="reparseTag">Etiqueta de reanálisis (<see cref="SourceEntry.ReparseTag"/>).</param>
    /// <returns>La clase del elemento.</returns>
    public static SourceEntryKind Classify(FileAttributes attributes, uint reparseTag)
    {
        var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        var tag = isReparsePoint ? reparseTag : 0u;
        if (ReparseTags.IsNameSurrogate(tag))
        {
            return SourceEntryKind.Link;
        }

        if ((attributes & (RecallOnOpen | RecallOnDataAccess)) != 0
            || ((attributes & FileAttributes.Offline) != 0 && (ReparseTags.IsCloud(tag) || ReparseTags.IsHsm(tag))))
        {
            return SourceEntryKind.CloudOnly;
        }

        if (!isReparsePoint)
        {
            return SourceEntryKind.Normal;
        }

        return ReparseTags.IsContentTag(tag) ? SourceEntryKind.Content : SourceEntryKind.Unreadable;
    }

    /// <summary>Clasifica un elemento enumerado (<see cref="Classify(FileAttributes, uint)"/>).</summary>
    /// <param name="entry">Elemento.</param>
    /// <returns>La clase del elemento.</returns>
    public static SourceEntryKind Classify(SourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Classify(entry.Attributes, entry.ReparseTag);
    }
}

/// <summary>Clase de un elemento de origen según sus atributos y su etiqueta de reanálisis (ADR-19, §8.2).</summary>
public enum SourceEntryKind
{
    /// <summary>Sin punto de reanálisis ni atributos de nube: se lee (una carpeta se recorre).</summary>
    Normal = 0,

    /// <summary>Etiqueta sustituto de nombre (<i>junction</i>, enlace simbólico, punto de montaje): no se sigue ni se conserva nada (RF-06).</summary>
    Link = 1,

    /// <summary>Solo en la nube (también una carpeta no poblada): no se abre; se conserva la versión del padre (R4).</summary>
    CloudOnly = 2,

    /// <summary>Etiqueta fuera de la lista blanca, o punto de reanálisis con etiqueta 0: no se abre; se conserva la versión del padre (RF-25).</summary>
    Unreadable = 3,

    /// <summary>Lista blanca (nube descargada, WOF, Dedup): se lee por su contenido (una carpeta de nube poblada se recorre).</summary>
    Content = 4,
}

/// <summary>Etiquetas de reanálisis (<c>IO_REPARSE_TAG_*</c>, <c>winnt.h</c>) que interpreta el motor (ADR-19).</summary>
public static class ReparseTags
{
    /// <summary>Bit sustituto de nombre (<c>IsReparseTagNameSurrogate</c>, bit N).</summary>
    public const uint NameSurrogateBit = 0x20000000;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: <i>junction</i> y punto de montaje.</summary>
    public const uint MountPoint = 0xA0000003;

    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>.</summary>
    public const uint Symlink = 0xA000000C;

    /// <summary><c>IO_REPARSE_TAG_HSM</c>.</summary>
    public const uint Hsm = 0xC0000004;

    /// <summary><c>IO_REPARSE_TAG_HSM2</c>.</summary>
    public const uint Hsm2 = 0x80000006;

    /// <summary><c>IO_REPARSE_TAG_DEDUP</c> (Data Deduplication).</summary>
    public const uint Dedup = 0x80000013;

    /// <summary><c>IO_REPARSE_TAG_WOF</c> (Windows Overlay Filter: CompactOS, compresión de WOF).</summary>
    public const uint Wof = 0x80000017;

    /// <summary><c>IO_REPARSE_TAG_CLOUD</c>; las variantes <c>CLOUD_1</c>…<c>CLOUD_F</c> usan los bits de <see cref="CloudMask"/>.</summary>
    public const uint Cloud = 0x9000001A;

    /// <summary><c>IO_REPARSE_TAG_CLOUD_MASK</c>: bits que distinguen las variantes de <see cref="Cloud"/>.</summary>
    public const uint CloudMask = 0x0000F000;

    /// <summary><c>IO_REPARSE_TAG_APPEXECLINK</c> (alias de ejecución de aplicaciones): no está en la lista blanca (R13).</summary>
    public const uint AppExecLink = 0x8000001B;

    /// <summary><c>IO_REPARSE_TAG_PROJFS</c> (Projected File System): no está en la lista blanca.</summary>
    public const uint ProjFs = 0x9000001C;

    /// <summary>Si la etiqueta es sustituto de nombre (enlace).</summary>
    public static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;

    /// <summary>Si la etiqueta es de la Cloud Files API (<c>IO_REPARSE_TAG_CLOUD*</c>: <c>tag &amp; 0xFFFF0FFF == 0x9000001A</c>).</summary>
    public static bool IsCloud(uint tag) => (tag & ~CloudMask) == Cloud;

    /// <summary>Si la etiqueta es de HSM (<see cref="Hsm"/> o <see cref="Hsm2"/>).</summary>
    public static bool IsHsm(uint tag) => tag is Hsm or Hsm2;

    /// <summary>Si la etiqueta es de la lista blanca, que se lee por su contenido.</summary>
    public static bool IsContentTag(uint tag) => IsCloud(tag) || tag is Wof or Dedup;

    /// <summary>La etiqueta en hexadecimal, para los avisos (<c>0x9000001C</c>).</summary>
    public static string Format(uint tag) => $"0x{tag:X8}";
}
