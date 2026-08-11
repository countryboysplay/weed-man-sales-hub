using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf.IO;
using SalesHub.Application.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// PDFsharp-based watermarking (docs/07): a diagonal identity+date stamp on
/// every page of a derived copy. Fonts resolve from the OS font directories
/// (Liberation/DejaVu on Linux, Arial on Windows Server).
/// </summary>
public sealed class PdfSharpWatermarker(ILogger<PdfSharpWatermarker> logger) : IPdfWatermarker
{
    private static readonly object FontInit = new();
    private static bool _fontResolverSet;

    public async Task<Stream> WatermarkAsync(
        Stream original, string watermarkText, CancellationToken cancellationToken = default)
    {
        EnsureFontsRegistered();

        // PDFsharp works on seekable streams.
        var buffer = new MemoryStream();
        await original.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        try
        {
            using var document = PdfReader.Open(buffer, PdfDocumentOpenMode.Modify);
            var font = new XFont("Liberation Sans", 16, XFontStyleEx.Bold);
            var brush = new XSolidBrush(XColor.FromArgb(70, 160, 160, 160));

            foreach (var page in document.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var state = gfx.Save();
                gfx.TranslateTransform(page.Width.Point / 2, page.Height.Point / 2);
                gfx.RotateTransform(-35);
                var size = gfx.MeasureString(watermarkText, font);
                // Repeat vertically so cropping cannot remove the identity.
                for (var offset = -2; offset <= 2; offset++)
                {
                    gfx.DrawString(watermarkText, font, brush,
                        new XPoint(-size.Width / 2, offset * 140));
                }

                gfx.Restore(state);
            }

            var output = new MemoryStream();
            document.Save(output, closeStream: false);
            output.Position = 0;
            return output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A malformed PDF must not take the viewer down; the caller still
            // enforced authorization and audit — deliver the original copy
            // and flag the failure loudly for ops.
            logger.LogError(ex, "PDF watermarking failed; serving unwatermarked copy");
            buffer.Position = 0;
            return buffer;
        }
    }

    /// <summary>Registers the font resolver once per process. Public so test
    /// hosts generating fixture PDFs share the same registration.</summary>
    public static void EnsureFontsRegistered()
    {
        if (_fontResolverSet)
        {
            return;
        }

        lock (FontInit)
        {
            if (_fontResolverSet)
            {
                return;
            }

            if (OperatingSystem.IsLinux() && GlobalFontSettings.FontResolver is null)
            {
                GlobalFontSettings.FontResolver = new LinuxFontResolver();
            }

            _fontResolverSet = true;
        }
    }

    /// <summary>Maps every request to Liberation Sans from the system fonts.</summary>
    private sealed class LinuxFontResolver : IFontResolver
    {
        private static readonly string[] Candidates =
        [
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        ];

        public byte[]? GetFont(string faceName)
        {
            var path = faceName == "sans-bold" ? Candidates[1] : Candidates[0];
            if (!File.Exists(path))
            {
                path = Candidates.FirstOrDefault(File.Exists)
                    ?? throw new InvalidOperationException("No usable TTF font found.");
            }

            return File.ReadAllBytes(path);
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            new(bold ? "sans-bold" : "sans-regular");
    }
}
