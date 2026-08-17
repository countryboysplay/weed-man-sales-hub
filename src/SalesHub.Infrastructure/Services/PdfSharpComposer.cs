using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SalesHub.Application.Abstractions;

namespace SalesHub.Infrastructure.Services;

/// <summary>Minimal PDFsharp document composer for export artifacts. Shares
/// the process-wide font registration with the watermarker.</summary>
public sealed class PdfSharpComposer : IPdfComposer
{
    public Task<Stream> ComposeAsync(
        string title,
        IReadOnlyList<(string Heading, IReadOnlyList<string> Lines)> sections,
        CancellationToken cancellationToken = default)
    {
        PdfSharpWatermarker.EnsureFontsRegistered();

        var document = new PdfDocument();
        var titleFont = new XFont("Liberation Sans", 16, XFontStyleEx.Bold);
        var headingFont = new XFont("Liberation Sans", 12, XFontStyleEx.Bold);
        var bodyFont = new XFont("Liberation Sans", 9, XFontStyleEx.Regular);

        var page = document.AddPage();
        var gfx = XGraphics.FromPdfPage(page);
        var y = 60.0;
        const double left = 50;
        const double lineHeight = 14;

        void NewPageIfNeeded()
        {
            if (y > page.Height.Point - 60)
            {
                gfx.Dispose();
                page = document.AddPage();
                gfx = XGraphics.FromPdfPage(page);
                y = 60;
            }
        }

        gfx.DrawString(title, titleFont, XBrushes.Black, new XPoint(left, y));
        y += 30;

        foreach (var (heading, lines) in sections)
        {
            NewPageIfNeeded();
            gfx.DrawString(heading, headingFont, XBrushes.Black, new XPoint(left, y));
            y += 20;
            foreach (var line in lines)
            {
                NewPageIfNeeded();
                var text = line.Length > 160 ? line[..160] + "…" : line;
                gfx.DrawString(text, bodyFont, XBrushes.Black, new XPoint(left, y));
                y += lineHeight;
            }

            y += 10;
        }

        gfx.Dispose();
        var output = new MemoryStream();
        document.Save(output, closeStream: false);
        output.Position = 0;
        return Task.FromResult<Stream>(output);
    }
}
