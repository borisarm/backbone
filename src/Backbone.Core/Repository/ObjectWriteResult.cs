namespace Backbone.Core.Repository;

/// <summary>Resultado de guardar un objeto.</summary>
/// <param name="Hash">SHA-256 de los bytes realmente escritos.</param>
/// <param name="Length">Bytes escritos.</param>
/// <param name="Stored">
/// <see langword="true"/> si el objeto se ha añadido; <see langword="false"/> si ya existía y el temporal se descartó.
/// </param>
/// <param name="ContentChanged">
/// <see langword="true"/> si se esperaba otro hash (el archivo cambió durante la copia sin VSS, §6.4.2):
/// el llamante debe usar <paramref name="Hash"/> y registrar el cambio.
/// </param>
public sealed record ObjectWriteResult(ContentHash Hash, long Length, bool Stored, bool ContentChanged);
