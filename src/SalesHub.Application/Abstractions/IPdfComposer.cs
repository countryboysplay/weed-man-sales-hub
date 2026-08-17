namespace SalesHub.Application.Abstractions;

/// <summary>
/// Server-side PDF generation for sensitive exports (docs/04): the browser
/// never reconstructs a sensitive artifact from client-side data. Sections
/// are simple headed line blocks — enough for history exports.
/// </summary>
public interface IPdfComposer
{
    Task<Stream> ComposeAsync(
        string title,
        IReadOnlyList<(string Heading, IReadOnlyList<string> Lines)> sections,
        CancellationToken cancellationToken = default);
}
