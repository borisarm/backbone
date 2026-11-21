using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using S = Backbone.Service.Os.NativeMethods.SourceIo;

namespace Backbone.Service.Os;

/// <summary>
/// Token de los hilos que leen orígenes (ADR-21, §8.2 "Token de lectura de los orígenes"): una <b>copia</b> del token del
/// proceso con <c>SeBackupPrivilege</c> activado y sin <c>SeRestorePrivilege</c>, <c>SeTakeOwnershipPrivilege</c>,
/// <c>SeSecurityPrivilege</c>, <c>SeDebugPrivilege</c> ni <c>SeImpersonatePrivilege</c>. Nunca se toca el token del proceso.
/// </summary>
/// <remarks>
/// Si algo falla al prepararla (duplicar, quitar, activar o la comprobación final), se descarta, se registra un aviso y los
/// hilos leen con el token del proceso sin activar nada (<see cref="HasBackupPrivilege"/> = <see langword="false"/>).
/// </remarks>
internal sealed partial class SourceReadToken : IDisposable
{
    /// <summary>Privilegios que se quitan de la copia (<c>SE_PRIVILEGE_REMOVED</c>, irreversible).</summary>
    internal static readonly string[] RemovedPrivileges =
    [
        "SeRestorePrivilege",
        "SeTakeOwnershipPrivilege",
        "SeSecurityPrivilege",
        "SeDebugPrivilege",
        "SeImpersonatePrivilege",
    ];

    internal const string BackupPrivilege = "SeBackupPrivilege";

    private readonly SafeAccessTokenHandle? _token;

    private SourceReadToken(SafeAccessTokenHandle? token) => _token = token;

    /// <summary>Sin copia: los hilos leen con el token del proceso (pruebas, o si la preparación falla).</summary>
    public static SourceReadToken Unprivileged { get; } = new(null);

    /// <summary>La copia se preparó y tiene <c>SeBackupPrivilege</c> activado.</summary>
    public bool HasBackupPrivilege => _token is { IsInvalid: false, IsClosed: false };

    /// <summary>Prepara la copia del token del proceso. Nunca lanza: si falla, devuelve <see cref="Unprivileged"/> con aviso.</summary>
    public static SourceReadToken Create(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        SafeAccessTokenHandle? copy = null;
        try
        {
            if (!S.OpenProcessToken(NativeMethods.Resolution.GetCurrentProcess(), NativeMethods.TokenDuplicate | NativeMethods.TokenQuery, out var processRaw))
            {
                return Fail(logger, "OpenProcessToken", Marshal.GetLastPInvokeError());
            }

            using var process = new SafeAccessTokenHandle(processRaw);
            if (!S.DuplicateTokenEx(process, S.TokenImpersonate | NativeMethods.TokenQuery | S.TokenAdjustPrivileges | NativeMethods.TokenDuplicate, 0, S.SecurityImpersonation, S.TokenTypeImpersonation, out var copyRaw))
            {
                return Fail(logger, "DuplicateTokenEx", Marshal.GetLastPInvokeError());
            }

            copy = new SafeAccessTokenHandle(copyRaw);
            foreach (var name in RemovedPrivileges)
            {
                // De uno en uno: quitar uno que el token no tiene da ERROR_NOT_ALL_ASSIGNED y no es un fallo.
                var error = Adjust(copy, name, S.SePrivilegeRemoved);
                if (error is not 0 and not S.ErrorNotAllAssigned)
                {
                    return Fail(logger, "AdjustTokenPrivileges(" + name + ", REMOVED)", error, copy);
                }
            }

            var enableError = Adjust(copy, BackupPrivilege, S.SePrivilegeEnabled);
            if (enableError != 0)
            {
                return Fail(logger, "AdjustTokenPrivileges(SeBackupPrivilege, ENABLED)", enableError, copy);
            }

            if (!Verify(copy, out var problem))
            {
                return Fail(logger, problem, 0, copy);
            }

            var result = new SourceReadToken(copy);
            copy = null;
            return result;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            copy?.Dispose();
            return Fail(logger, ex.GetType().Name, 0);
        }
    }

    /// <summary>
    /// Comprueba con <c>GetTokenInformation(TokenPrivileges)</c> que el token no tiene ninguno de los cinco privilegios
    /// quitados y tiene <c>SeBackupPrivilege</c> activado.
    /// </summary>
    internal static bool Verify(SafeHandle token, out string problem)
    {
        var privileges = ReadPrivileges(token);
        if (privileges is null)
        {
            problem = "GetTokenInformation(TokenPrivileges)";
            return false;
        }

        foreach (var name in RemovedPrivileges)
        {
            if (!LookupLuid(name, out var luid))
            {
                problem = "LookupPrivilegeValue(" + name + ")";
                return false;
            }

            if (privileges.Any(p => p.Luid.LowPart == luid.LowPart && p.Luid.HighPart == luid.HighPart))
            {
                problem = name + " is still present";
                return false;
            }
        }

        if (!LookupLuid(BackupPrivilege, out var backup)
            || !privileges.Any(p => p.Luid.LowPart == backup.LowPart && p.Luid.HighPart == backup.HighPart && (p.Attributes & S.SePrivilegeEnabled) != 0))
        {
            problem = "SeBackupPrivilege is not enabled";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>Privilegios del token (LUID y atributos), o <see langword="null"/> si no se pueden leer.</summary>
    internal static IReadOnlyList<(NativeMethods.Luid Luid, uint Attributes)>? ReadPrivileges(SafeHandle token)
    {
        _ = S.GetTokenInformation(token, S.TokenPrivilegesClass, null, 0, out var needed);
        if (needed is 0 or > 64 * 1024)
        {
            return null;
        }

        var buffer = new byte[needed];
        if (!S.GetTokenInformation(token, S.TokenPrivilegesClass, buffer, needed, out _))
        {
            return null;
        }

        // TOKEN_PRIVILEGES: DWORD PrivilegeCount; LUID_AND_ATTRIBUTES Privileges[] (LUID de 8 bytes + DWORD, 12 bytes cada uno).
        var count = BitConverter.ToUInt32(buffer, 0);
        if (count > (buffer.Length - 4) / 12)
        {
            return null;
        }

        var list = new List<(NativeMethods.Luid, uint)>((int)count);
        for (var i = 0; i < count; i++)
        {
            var offset = 4 + (i * 12);
            list.Add((new NativeMethods.Luid { LowPart = BitConverter.ToUInt32(buffer, offset), HighPart = BitConverter.ToInt32(buffer, offset + 4) }, BitConverter.ToUInt32(buffer, offset + 8)));
        }

        return list;
    }

    /// <summary>
    /// Asigna la copia al hilo actual (<c>SetThreadToken</c>); el resultado llama a <c>RevertToSelf</c> al desecharse. Sin
    /// copia no hace nada. Solo para los hilos dedicados de lectura (<see cref="SourceReaderThread"/>).
    /// </summary>
    public IDisposable ApplyToCurrentThread(ILogger? logger = null)
    {
        if (!HasBackupPrivilege || _token is null)
        {
            return NoRevert.Instance;
        }

        if (!S.SetThreadToken(0, _token))
        {
            LogApplyFailed(logger ?? NullLogger.Instance, Marshal.GetLastPInvokeError());
            return NoRevert.Instance;
        }

        return new Revert();
    }

    public void Dispose()
    {
        if (!ReferenceEquals(this, Unprivileged))
        {
            _token?.Dispose();
        }
    }

    private static int Adjust(SafeHandle token, string name, uint attributes)
    {
        if (!LookupLuid(name, out var luid))
        {
            return Marshal.GetLastPInvokeError() is var e and not 0 ? e : -1;
        }

        var state = new S.TokenPrivilege { PrivilegeCount = 1, Luid = luid, Attributes = attributes };
        if (!S.AdjustTokenPrivileges(token, false, in state, 0, 0, 0))
        {
            return Marshal.GetLastPInvokeError();
        }

        return Marshal.GetLastPInvokeError() == S.ErrorNotAllAssigned ? S.ErrorNotAllAssigned : 0;
    }

    private static bool LookupLuid(string name, out NativeMethods.Luid luid) => S.LookupPrivilegeValue(null, name, out luid);

    private static SourceReadToken Fail(ILogger logger, string step, int error, SafeAccessTokenHandle? copy = null)
    {
        copy?.Dispose();
        LogPrepareFailed(logger, step, error);
        return Unprivileged;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido preparar el token de lectura de orígenes con SeBackupPrivilege ({Step}, error {Error}); se lee sin el privilegio y los archivos cuya DACL no da acceso a SYSTEM darán acceso denegado (ADR-21).")]
    private static partial void LogPrepareFailed(ILogger logger, string step, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido asignar el token de lectura de orígenes al hilo (SetThreadToken, error {Error}); el hilo lee sin el privilegio.")]
    private static partial void LogApplyFailed(ILogger logger, int error);

    private sealed class NoRevert : IDisposable
    {
        public static readonly NoRevert Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Revert : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                _ = S.RevertToSelf();
            }
        }
    }
}

/// <summary>
/// Hilos dedicados de lectura de orígenes (ADR-21): se crean con <c>new Thread</c> y se arrancan con <c>UnsafeStart</c>
/// (no capturan el <c>ExecutionContext</c>: no viaja ninguna suplantación del hilo que los pide), asignan el token de
/// <see cref="SourceReadToken"/> al empezar y llaman a <c>RevertToSelf</c> al terminar. El cuerpo es síncrono.
/// </summary>
internal static class SourceReaderThread
{
    public static Thread Start(string name, SourceReadToken token, Action body, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(body);
        var thread = new Thread(() =>
        {
            using (token.ApplyToCurrentThread(logger))
            {
                body();
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.UnsafeStart();
        return thread;
    }
}
