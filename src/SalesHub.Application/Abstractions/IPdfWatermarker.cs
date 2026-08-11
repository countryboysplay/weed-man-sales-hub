namespace SalesHub.Application.Abstractions;

/// <summary>
/// Derives a watermarked copy of a PDF (docs/07): the employee viewer and
/// manager downloads carry the viewer's identity and date on every page.
/// The original blob is never modified.
/// </summary>
public interface IPdfWatermarker
{
    Task<Stream> WatermarkAsync(
        Stream original, string watermarkText, CancellationToken cancellationToken = default);
}
