using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Backbone.Core.Engine;
using Backbone.Service.Os;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Backbone.IntegrationTests.Sources;

/// <summary>
/// Revisión de seguridad del <c>SourceFileSystem</c> de HU-08 con el sistema de archivos real: las aperturas no esperan la
/// ruptura de un oplock (M-1, <c>FILE_COMPLETE_IF_OPLOCKED</c>) y, con el token de ADR-21, se lee un archivo cuya DACL no da
/// acceso a la cuenta (este caso necesita PowerShell elevado: sin <c>SeBackupPrivilege</c> se omite).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HU-08")]
public sealed partial class SourceReadSecurityIntegrationTests : IDisposable
{
    private const uint FsctlRequestBatchOplock = 0x0009_0008;
    private const int ErrorIoPending = 997;

    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly List<SourceFileSystem> _created = [];

    public SourceReadSecurityIntegrationTests()
    {
        _root = Directory.CreateDirectory(_temp.Combine("origen")).FullName;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var fs in _created)
        {
            fs.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>
    /// Un usuario con escritura en el origen pone un oplock <i>batch</i> sobre su archivo y no confirma la ruptura. La
    /// apertura de SYSTEM termina enseguida (<c>STATUS_OPLOCK_BREAK_IN_PROGRESS</c> es un éxito) en lugar de quedarse
    /// esperando; la ruptura se ha pedido (se señala la notificación) y, cuando el dueño cierra, se lee el contenido.
    /// </summary>
    [Fact]
    public async Task M1_An_open_completes_at_once_while_a_batch_oplock_on_the_file_is_not_acknowledged()
    {
        var content = RandomNumberGenerator.GetBytes(64 * 1024);
        var path = Path.Combine(_root, "con-oplock.bin");
        await File.WriteAllBytesAsync(path, content, Ct);
        var fs = Create(SourceReadToken.Unprivileged);
        var readPath = SourceHandles.FinalPath(_root);

        var owner = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        using var oplock = BatchOplock.Request(owner);
        if (oplock is null)
        {
            owner.Dispose();
            Assert.Skip("The file system did not grant a batch oplock to this process.");
        }

        Stream? stream = null;
        try
        {
            var open = fs.OpenReadAsync(readPath + @"\con-oplock.bin", Ct);
            var completed = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(10), Ct)) == open;

            Assert.True(completed, "The open waited for the oplock break to be acknowledged.");
            stream = await open;
            Assert.True(oplock.BreakRequested.WaitOne(TimeSpan.FromSeconds(5)), "The open did not request the oplock break.");
        }
        finally
        {
            owner.Dispose(); // confirma la ruptura al cerrar
        }

        await using (stream)
        {
            using var memory = new MemoryStream();
            await stream!.CopyToAsync(memory, Ct);
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(memory.ToArray()));
        }
    }

    /// <summary>
    /// ADR-21: con <c>SeBackupPrivilege</c> activado solo en la copia del token del hilo de lectura, se lee un archivo cuya
    /// DACL protegida solo da acceso a SYSTEM; sin el token (lectura con el del proceso), el mismo archivo da acceso denegado.
    /// Requiere PowerShell elevado.
    /// </summary>
    [Fact]
    public async Task ADR21_With_the_read_token_a_file_whose_DACL_denies_the_account_is_read_and_without_it_it_is_access_denied()
    {
        using var token = SourceReadToken.Create(NullLogger.Instance);
        if (!token.HasBackupPrivilege)
        {
            Assert.Skip("SeBackupPrivilege is not available: run the integration tests from an elevated PowerShell.");
        }

        var content = RandomNumberGenerator.GetBytes(4096);
        var path = Path.Combine(_root, "solo-system.bin");
        await File.WriteAllBytesAsync(path, content, Ct);
        var file = new FileInfo(path);
        var original = file.GetAccessControl();
        var onlySystem = new FileSecurity();
        onlySystem.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        onlySystem.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(onlySystem);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(path));
            var readPath = SourceHandles.FinalPath(_root);

            var withoutToken = Create(SourceReadToken.Unprivileged);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => withoutToken.OpenReadAsync(readPath + @"\solo-system.bin", Ct));

            var withToken = Create(token);
            await using var stream = await withToken.OpenReadAsync(readPath + @"\solo-system.bin", Ct);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, Ct);
            Assert.Equal(content, memory.ToArray());
        }
        finally
        {
            file.SetAccessControl(original); // el propietario (la cuenta) siempre puede cambiar la DACL
        }
    }

    private SourceFileSystem Create(SourceReadToken token)
    {
        var readPath = SourceHandles.FinalPath(_root);
        var fs = new SourceFileSystem(
            [new BackupSource("origen", _root, readPath)],
            new Win32SourceFileApi(),
            TimeProvider.System,
            SourceReadOptions.Default,
            NullLoggerFactory.Instance.CreateLogger<SourceFileSystem>(),
            token);
        _created.Add(fs);
        return fs;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, uint* bytesReturned, NativeOverlapped* overlapped);

    /// <summary>Oplock <i>batch</i> pedido con <c>FSCTL_REQUEST_BATCH_OPLOCK</c>; el evento se señala al pedirse la ruptura.</summary>
    private sealed unsafe class BatchOplock : IDisposable
    {
        private readonly NativeOverlapped* _overlapped;

        private BatchOplock(NativeOverlapped* overlapped, ManualResetEvent breakRequested)
        {
            _overlapped = overlapped;
            BreakRequested = breakRequested;
        }

        public ManualResetEvent BreakRequested { get; }

        public static BatchOplock? Request(SafeFileHandle handle)
        {
            var signal = new ManualResetEvent(initialState: false);
            var overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            overlapped->EventHandle = signal.SafeWaitHandle.DangerousGetHandle() | 1; // sin puerto de finalización
            if (DeviceIoControl(handle, FsctlRequestBatchOplock, null, 0, null, 0, null, overlapped) || Marshal.GetLastPInvokeError() != ErrorIoPending)
            {
                NativeMemory.Free(overlapped);
                signal.Dispose();
                return null;
            }

            return new BatchOplock(overlapped, signal);
        }

        public void Dispose()
        {
            // La operación termina al cerrarse el handle del dueño; se espera un poco antes de liberar el OVERLAPPED.
            BreakRequested.WaitOne(TimeSpan.FromSeconds(5));
            NativeMemory.Free(_overlapped);
            BreakRequested.Dispose();
        }
    }
}
