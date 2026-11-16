using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static Backbone.Service.Os.NativeMethods;

namespace Backbone.Service.Os;

/// <summary>
/// Cifrado de secretos en reposo (ADR-07). En producción, <see cref="DpapiMachineProtector"/>; las pruebas pueden usar un
/// doble.
/// </summary>
internal interface IDataProtector
{
    /// <summary>Cifra <paramref name="plaintext"/> con la entropía indicada.</summary>
    /// <exception cref="CryptographicException">No se ha podido cifrar.</exception>
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy);

    /// <summary>
    /// Descifra <paramref name="ciphertext"/>. El resultado es texto en claro en un array <b>fijado en memoria</b> (el GC no
    /// lo copia): quien lo recibe lo pone a cero en cuanto termina (<see cref="CryptographicOperations.ZeroMemory"/>).
    /// </summary>
    /// <exception cref="CryptographicException">No se puede descifrar (otra entropía, otro equipo, datos alterados).</exception>
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy);
}

/// <summary>
/// DPAPI con ámbito <b>equipo</b> (<c>CRYPTPROTECT_LOCAL_MACHINE</c>) y sin interfaz (<c>CRYPTPROTECT_UI_FORBIDDEN</c>), por
/// P/Invoke a <c>crypt32.dll</c> (ADR-07). Equivale a <c>ProtectedData</c> con <c>DataProtectionScope.LocalMachine</c> sin
/// depender del paquete <c>System.Security.Cryptography.ProtectedData</c>.
/// </summary>
/// <remarks>
/// Con ámbito equipo, cualquier proceso del equipo que conozca la entropía puede descifrar: la confidencialidad descansa en
/// la ACL de <c>credentials\</c> (solo SYSTEM y Administradores, RNF-07) y en que el cifrado solo sirve en este equipo. No
/// necesita el perfil ni la sesión de ningún usuario, así que funciona en una ejecución programada sin sesión iniciada
/// (HU-04, criterio 5). El texto en claro que devuelve el sistema se pone a cero antes de liberarlo.
/// </remarks>
internal sealed class DpapiMachineProtector : IDataProtector
{
    /// <summary>Indicadores con que se cifra.</summary>
    internal const uint ProtectFlags = CryptProtectLocalMachine | CryptProtectUiForbidden;

    /// <summary>Indicadores con que se descifra (el ámbito va en el propio blob).</summary>
    internal const uint UnprotectFlags = CryptProtectUiForbidden;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
        Call(plaintext, entropy, protect: true, ProtectFlags);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
        Call(ciphertext, entropy, protect: false, UnprotectFlags);

    /// <summary>Llamada a DPAPI con indicadores explícitos (también para comprobar el ámbito en pruebas).</summary>
    internal static unsafe byte[] Call(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy, bool protect, uint flags)
    {
        if (entropy.IsEmpty)
        {
            throw new ArgumentException("An application-specific entropy is required.", nameof(entropy));
        }

        fixed (byte* inputPointer = input)
        fixed (byte* entropyPointer = entropy)
        {
            var dataIn = new DataBlob { Size = (uint)input.Length, Data = (nint)inputPointer };
            var entropyBlob = new DataBlob { Size = (uint)entropy.Length, Data = (nint)entropyPointer };
            DataBlob dataOut;
            var ok = protect
                ? CryptProtectData(in dataIn, 0, in entropyBlob, 0, 0, flags, out dataOut)
                : CryptUnprotectData(in dataIn, 0, in entropyBlob, 0, 0, flags, out dataOut);
            if (!ok)
            {
                // Solo el código Win32: nunca datos de entrada ni de salida.
                throw new CryptographicException(
                    protect ? "The data could not be protected." : "The data could not be unprotected.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            try
            {
                var output = new Span<byte>((void*)dataOut.Data, checked((int)dataOut.Size));

                // Fijado: el texto en claro no se mueve (ni deja copias) por la compactación del GC antes de ponerlo a cero.
                var result = GC.AllocateUninitializedArray<byte>(output.Length, pinned: true);
                output.CopyTo(result);
                return result;
            }
            finally
            {
                Release(dataOut.Data, dataOut.Size, &ZeroUnmanaged, &FreeLocal);
            }
        }
    }

    /// <summary>
    /// Pone a cero y libera el <c>pbData</c> de salida de DPAPI. <paramref name="free"/> se llama <b>siempre</b> que haya
    /// puntero, aunque poner a cero falle, y el tamaño se trata como <see cref="uint"/> completo (sin convertirlo a
    /// <see cref="int"/>, que con más de 2 GiB sería negativo).
    /// </summary>
    internal static unsafe void Release(nint data, uint size, delegate*<nint, uint, void> zero, delegate*<nint, void> free)
    {
        if (data == 0)
        {
            return;
        }

        try
        {
            zero(data, size);
        }
        finally
        {
            free(data);
        }
    }

    /// <summary>Pone a cero <paramref name="size"/> bytes no administrados, por tramos que caben en un <see cref="Span{T}"/>.</summary>
    internal static unsafe void ZeroUnmanaged(nint data, uint size)
    {
        var pointer = (byte*)data;
        var remaining = (ulong)size;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, int.MaxValue);
            CryptographicOperations.ZeroMemory(new Span<byte>(pointer, chunk));
            pointer += chunk;
            remaining -= (ulong)chunk;
        }
    }

    private static void FreeLocal(nint data) => LocalFree(data);
}
