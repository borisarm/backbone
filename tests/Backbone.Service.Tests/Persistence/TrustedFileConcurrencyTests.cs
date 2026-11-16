using System.ComponentModel;
using System.Diagnostics;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// HU-04 (F-1 de QA): <see cref="TrustedFile"/> con lectores concurrentes a un guardado (renombrado atómico) o a un borrado
/// del mismo archivo, sin ninguna serialización por encima (como <c>settings.json</c> y <c>jobs.json</c>). El lector ve el
/// contenido anterior completo, el nuevo completo o ninguno; nunca un error.
/// </summary>
[Trait("Story", "HU-04")]
public sealed class TrustedFileConcurrencyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TrustedFile _files = TestFileTrust.CurrentUser;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Readers_concurrent_with_atomic_replacements_always_see_one_complete_version()
    {
        var path = _temp.Combine("settings.json");
        var a = Enumerable.Repeat((byte)'a', 4096).ToArray();
        var b = Enumerable.Repeat((byte)'b', 8192).ToArray();
        await _files.WriteAllBytesAsync(path, a, Ct);

        var errors = await RunReadersWhileAsync(path, async () =>
        {
            for (var i = 0; i < 60; i++)
            {
                await _files.WriteAllBytesAsync(path, i % 2 == 0 ? b : a, Ct);
            }
        }, content => Assert.True(content is not null && (content.SequenceEqual(a) || content.SequenceEqual(b))));

        Assert.True(errors.Count == 0, Describe(errors));
        Assert.Single(Directory.GetFileSystemEntries(_temp.Path));
    }

    [Fact]
    public async Task Readers_concurrent_with_replacements_and_deletes_see_a_complete_version_or_none()
    {
        var path = _temp.Combine("jobs.json");
        var a = Enumerable.Repeat((byte)'a', 4096).ToArray();

        var errors = await RunReadersWhileAsync(path, async () =>
        {
            for (var i = 0; i < 60; i++)
            {
                await _files.WriteAllBytesAsync(path, a, Ct);
                Assert.True(_files.DeleteIfExists(path));
            }
        }, content => Assert.True(content is null || content.SequenceEqual(a)));

        Assert.True(errors.Count == 0, Describe(errors));
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Path));
    }

    [Fact]
    public async Task A_delete_removes_the_name_at_once_even_while_a_reader_still_has_the_file_open()
    {
        var path = _temp.Combine("settings.json");
        await _files.WriteAllBytesAsync(path, "antiguo"u8.ToArray(), Ct);

        // Lector que mantiene el archivo abierto (con FILE_SHARE_DELETE, como TrustedFile).
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.True(_files.DeleteIfExists(path));

            // Semántica POSIX: el nombre ya no existe, aunque el lector siga abierto, y no queda "pendiente de borrado".
            Assert.Empty(Directory.GetFileSystemEntries(_temp.Path));
            Assert.Null(await _files.ReadAllBytesAsync(path, 1024, Ct));
            Assert.False(_files.DeleteIfExists(path));
            await _files.WriteAllBytesAsync(path, "nuevo"u8.ToArray(), Ct);

            // El lector sigue leyendo el contenido anterior completo.
            using var text = new StreamReader(reader);
            Assert.Equal("antiguo", await text.ReadToEndAsync(Ct));
        }

        Assert.Equal("nuevo"u8.ToArray(), await _files.ReadAllBytesAsync(path, 1024, Ct));
    }

    [Fact]
    public async Task A_persistent_sharing_violation_is_retried_for_a_bounded_time_and_reported_with_its_Win32_code()
    {
        var path = _temp.Combine("settings.json");
        await _files.WriteAllBytesAsync(path, "{}"u8.ToArray(), Ct);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<IOException>(() => _files.ReadAllBytesAsync(path, 1024, Ct));
        watch.Stop();

        Assert.Contains("Win32 error 32", ex.Message, StringComparison.Ordinal);
        Assert.Equal(32, Assert.IsType<Win32Exception>(ex.InnerException).NativeErrorCode);
        Assert.Equal(unchecked((int)0x80070020), ex.HResult);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5)); // acotado (≈0,5 s)
    }

    [Fact]
    public async Task A_cancelled_read_stops_retrying()
    {
        var path = _temp.Combine("settings.json");
        await _files.WriteAllBytesAsync(path, "{}"u8.ToArray(), Ct);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _files.ReadAllBytesAsync(path, 1024, cts.Token));
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(5, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(87, false)]
    public void Only_sharing_violations_and_delete_pending_access_denied_are_transient(int error, bool transient)
    {
        Assert.Equal(transient, TrustedFile.IsTransientOpenError(error));
    }

    private async Task<List<Exception>> RunReadersWhileAsync(string path, Func<Task> writer, Action<byte[]?> check)
    {
        var errors = new List<Exception>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        async Task Reader()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    check(await _files.ReadAllBytesAsync(path, 1024 * 1024, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(ex);
                    }
                }
            }
        }

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(Reader, Ct)).ToArray();
        try
        {
            await writer();
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(readers);
        }

        return errors;
    }

    private static string Describe(List<Exception> errors) =>
        string.Join(Environment.NewLine, errors.Select(e => e.GetType().Name + ": " + e.Message).Distinct());
}
