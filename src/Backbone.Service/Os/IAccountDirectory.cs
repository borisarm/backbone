using System.Security.Principal;

namespace Backbone.Service.Os;

/// <summary>Tipo de cuenta según <c>SID_NAME_USE</c>, reducido a lo que importa para §7.3.</summary>
internal enum AccountKind
{
    /// <summary>Cualquier cosa que no sea una cuenta de usuario (grupo, alias, SID bien conocido, equipo…).</summary>
    Other = 0,

    /// <summary>Cuenta de usuario (<c>SidTypeUser</c>).</summary>
    User = 1,
}

/// <summary>Cuenta resuelta por el sistema.</summary>
/// <param name="Sid">SID de la cuenta.</param>
/// <param name="AccountName">Nombre cualificado <c>DOMINIO\usuario</c>.</param>
/// <param name="Kind">Tipo de cuenta.</param>
internal sealed record AccountInfo(SecurityIdentifier Sid, string AccountName, AccountKind Kind);

/// <summary>
/// Consulta de cuentas y de la pertenencia <b>actual</b> al grupo local Administradores (§7.3). Implementación real con la
/// API de Windows (<see cref="WindowsAccountDirectory"/>); en pruebas, un doble.
/// </summary>
internal interface IAccountDirectory
{
    /// <summary>Resuelve una cuenta cualificada (<c>EQUIPO\usuario</c>, <c>DOMINIO\usuario</c>); <see langword="null"/> si no existe.</summary>
    AccountInfo? LookupByName(string qualifiedName);

    /// <summary>Resuelve un SID; <see langword="null"/> si no corresponde a ninguna cuenta.</summary>
    AccountInfo? LookupBySid(SecurityIdentifier sid);

    /// <summary>
    /// Indica si la cuenta es miembro, directo o mediante grupos anidados, del grupo local Administradores <b>ahora</b>
    /// (consultado al sistema, no al token del cliente). Lanza si la consulta falla; quien llama deniega en ese caso.
    /// </summary>
    bool IsLocalAdministrator(SecurityIdentifier sid);
}
