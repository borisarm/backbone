using System.Security.Cryptography;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Repository;

/// <summary>HT-04 criterio 2: objetos escritos en tmp/ con hash calculado mientras se copian y renombrados.</summary>
[Trait("Story", "HT-04")]
public sealed class ObjectStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData((3 * 1024 * 1024) + 17)]
    public async Task HT04_2_WriteObject_stores_content_under_its_sha256(int size)
    {
        var (storage, repository, repositoryLock) = await CreateAsync();
        var content = RandomBytes(size, seed: size);

        var result = await repository.WriteObjectAsync(new MemoryStream(content), null, repositoryLock, Ct);

        var expected = Convert.ToHexStringLower(SHA256.HashData(content));
        Assert.Equal(expected, result.Hash.ToString());
        Assert.Equal(size, result.Length);
        Assert.True(result.Stored);
        Assert.False(result.ContentChanged);
        Assert.Equal(content, storage.GetFileContent($"objects/{expected[..2]}/{expected}"));
        Assert.True(await repository.ObjectExistsAsync(result.Hash, Ct));
        AssertTmpEmpty(storage);

        await using var read = await repository.OpenObjectAsync(result.Hash, Ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, Ct);
        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public async Task HT04_2_Existing_object_is_not_rewritten_and_the_temporary_is_discarded()
    {
        var (storage, repository, repositoryLock) = await CreateAsync();
        var content = RandomBytes(5000, seed: 1);
        var first = await repository.WriteObjectAsync(new MemoryStream(content), null, repositoryLock, Ct);
        var path = $"objects/{first.Hash.Prefix}/{first.Hash}";
        var lastWrite = (await storage.GetFileInfoAsync(path, Ct))?.LastWriteUtc;

        var second = await repository.WriteObjectAsync(new MemoryStream(content), first.Hash, repositoryLock, Ct);

        Assert.Equal(first.Hash, second.Hash);
        Assert.False(second.Stored);
        Assert.False(second.ContentChanged);
        Assert.Equal(lastWrite, (await storage.GetFileInfoAsync(path, Ct))?.LastWriteUtc);
        AssertTmpEmpty(storage);
    }

    [Fact]
    public async Task HT04_2_Hash_is_computed_from_the_bytes_written_and_a_change_is_reported()
    {
        var (storage, repository, repositoryLock) = await CreateAsync();
        var inspected = ContentHash.FromBytes(SHA256.HashData("versión inspeccionada"u8));
        var actual = "versión que se copió después de cambiar"u8.ToArray();

        var result = await repository.WriteObjectAsync(new MemoryStream(actual), inspected, repositoryLock, Ct);

        Assert.True(result.ContentChanged);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(actual)), result.Hash.ToString());
        Assert.Equal(actual, storage.GetFileContent(RepositoryLayout.ObjectPath(result.Hash)));
    }

    [Fact]
    public async Task HT04_2_Source_that_changes_while_being_read_gets_the_hash_of_what_was_written()
    {
        // El origen devuelve bytes distintos en cada lectura (archivo que cambia sin VSS).
        var (storage, repository, repositoryLock) = await CreateAsync();
        var source = new MutatingStream(chunks: 3, chunkSize: 1000);

        var result = await repository.WriteObjectAsync(source, null, repositoryLock, Ct);

        var stored = storage.GetFileContent(RepositoryLayout.ObjectPath(result.Hash));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stored)), result.Hash.ToString());
        Assert.Equal(source.Produced.ToArray(), stored);
    }

    [Fact]
    public async Task HT04_2_Cancellation_during_copy_leaves_no_object_and_no_temporary()
    {
        var (storage, repository, repositoryLock) = await CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var source = new CancellingStream(RandomBytes(3 * 1024 * 1024, seed: 3), cancellation, cancelAfterReads: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.WriteObjectAsync(source, null, repositoryLock, cancellation.Token));

        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("objects/", StringComparison.Ordinal));
        AssertTmpEmpty(storage);
    }

    [Fact]
    public async Task HT04_2_Open_missing_object_is_ObjectNotFound()
    {
        var (_, repository, _) = await CreateAsync();

        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            repository.OpenObjectAsync(ContentHash.FromBytes(new byte[32]), Ct));

        Assert.Equal(RepositoryErrorKind.ObjectNotFound, ex.Kind);
    }

    [Fact]
    public async Task HT04_2_Writing_requires_a_live_lock_on_the_same_repository()
    {
        var (_, repository, repositoryLock) = await CreateAsync();
        var other = new InMemoryRepositoryStorage();
        var otherRepository = await BackupRepository.InitializeAsync(other, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            otherRepository.WriteObjectAsync(new MemoryStream([1]), null, repositoryLock, Ct));

        await repositoryLock.ReleaseAsync(Ct);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            repository.WriteObjectAsync(new MemoryStream([1]), null, repositoryLock, Ct));
        Assert.Equal(RepositoryErrorKind.LockLost, ex.Kind);
    }

    [Fact]
    public async Task HT04_2_Objects_work_on_the_file_system()
    {
        using var directory = new TempDirectory();
        var storage = FileSystemRepositoryStorage.ForLocalTesting(directory.Path);
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, TimeProvider.System, Ct);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PC", 1), Ct);
        var content = RandomBytes(2 * 1024 * 1024, seed: 9);

        var first = await repository.WriteObjectAsync(new MemoryStream(content), null, repositoryLock, Ct);
        var second = await repository.WriteObjectAsync(new MemoryStream(content), null, repositoryLock, Ct);

        Assert.True(first.Stored);
        Assert.False(second.Stored);
        var hex = first.Hash.ToString();
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Join(directory.Path, "objects", hex[..2], hex), Ct));
        Assert.Empty(Directory.EnumerateFiles(Path.Join(directory.Path, "tmp")));
    }

    internal static async Task<(InMemoryRepositoryStorage Storage, BackupRepository Repository, RepositoryLock Lock)> CreateAsync()
    {
        var storage = new InMemoryRepositoryStorage();
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, new ManualTimeProvider(), Ct);
        var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PC", 1), Ct);
        return (storage, repository, repositoryLock);
    }

    internal static byte[] RandomBytes(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void AssertTmpEmpty(InMemoryRepositoryStorage storage) =>
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));

    private sealed class MutatingStream(int chunks, int chunkSize) : Stream
    {
        private int remaining = chunks;
        private byte value;

        public MemoryStream Produced { get; } = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining-- <= 0)
            {
                return 0;
            }

            var n = Math.Min(count, chunkSize);
            buffer.AsSpan(offset, n).Fill(value++);
            Produced.Write(buffer, offset, n);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellingStream(byte[] content, CancellationTokenSource cancellation, int cancelAfterReads) : MemoryStream(content)
    {
        private int reads;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++reads > cancelAfterReads)
            {
                cancellation.Cancel();
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
