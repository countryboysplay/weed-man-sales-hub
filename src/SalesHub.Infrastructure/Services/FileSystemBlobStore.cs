using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SalesHub.Application.Abstractions;
using SalesHub.Domain;
using SalesHub.Domain.Entities;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Infrastructure.Services;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>
    /// Root directory for blob content. Must live outside the deployment
    /// directory in production (docs/07/08), e.g. D:\WeedManSalesHub\data\uploads.
    /// </summary>
    public string RootPath { get; set; } = "data/uploads";
}

/// <summary>
/// Immutable blob store on the local filesystem (docs/07). Storage keys are
/// server-generated (blob id + date sharding); originals are never mutated —
/// replace means a new blob. Content is streamed to a temp file, hashed
/// SHA-256, then moved into place.
/// </summary>
public sealed class FileSystemBlobStore(
    SalesHubDbContext db,
    IOptions<FileStorageOptions> options,
    BusinessTime businessTime) : IFileBlobStore
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    public async Task<FileBlob> SaveAsync(
        Stream content,
        string originalName,
        string contentType,
        Guid? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.CreateVersion7();
        var now = businessTime.UtcNow;
        var storageKey = $"{now:yyyy/MM}/{id:N}";
        var finalPath = Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var tempPath = finalPath + ".tmp";
        long length;
        string sha256;
        await using (var file = new FileStream(
            tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            length = 0;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                length += read;
            }

            sha256 = Convert.ToHexString(hasher.GetHashAndReset());
        }

        File.Move(tempPath, finalPath, overwrite: false);

        var blob = new FileBlob
        {
            Id = id,
            Sha256 = sha256,
            ContentType = contentType,
            OriginalName = Path.GetFileName(originalName),
            ByteLength = length,
            StorageKey = storageKey,
            CreatedAtUtc = now,
            CreatedByUserId = createdByUserId,
            ScanStatus = "Pending",
        };
        db.FileBlobs.Add(blob);
        await db.SaveChangesAsync(cancellationToken);
        return blob;
    }

    public async Task<Stream> OpenReadAsync(Guid blobId, CancellationToken cancellationToken = default)
    {
        var blob = await db.FileBlobs.FindAsync([blobId], cancellationToken)
            ?? throw new FileNotFoundException($"Blob {blobId} not found.");
        if (blob.DeletedAtUtc is not null)
        {
            throw new FileNotFoundException($"Blob {blobId} is deleted.");
        }

        var path = Path.Combine(_root, blob.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        return new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }
}
