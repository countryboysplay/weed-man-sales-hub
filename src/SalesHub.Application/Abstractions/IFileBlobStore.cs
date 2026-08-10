using SalesHub.Domain.Entities;

namespace SalesHub.Application.Abstractions;

/// <summary>
/// Immutable blob storage (docs/07): server-generated storage keys, SHA-256
/// hashing, content outside the deployment directory. Replace = new blob.
/// </summary>
public interface IFileBlobStore
{
    Task<FileBlob> SaveAsync(
        Stream content,
        string originalName,
        string contentType,
        Guid? createdByUserId,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(Guid blobId, CancellationToken cancellationToken = default);
}
