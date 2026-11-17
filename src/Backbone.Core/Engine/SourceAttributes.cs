namespace Backbone.Core.Engine;

/// <summary>Atributos de origen que el motor interpreta (RF-06, R4).</summary>
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

    /// <summary>Atributos que hacen que un archivo se considere "solo en la nube" (R4): los dos anteriores y <see cref="FileAttributes.Offline"/>.</summary>
    public const FileAttributes CloudOnlyMask = RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline;

    /// <summary>
    /// Si un <b>archivo</b> con esos atributos está solo en la nube (o en almacenamiento remoto) y abrirlo o leerlo
    /// desencadenaría su descarga (R4, RF-06). El motor no lo abre: lo omite con <c>CloudOnlySkipped</c>.
    /// </summary>
    /// <param name="attributes">Atributos devueltos por la enumeración.</param>
    /// <returns>Si tiene alguno de <see cref="CloudOnlyMask"/>.</returns>
    public static bool IsCloudOnly(FileAttributes attributes) => (attributes & CloudOnlyMask) != 0;
}
