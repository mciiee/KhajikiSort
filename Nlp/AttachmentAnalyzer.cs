using System.Text;
using KhajikiSort.Models;

namespace KhajikiSort.Nlp;

public static class AttachmentAnalyzer
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic"
    };

    public static AttachmentInsights Analyze(string raw)
    {
        var tokens = SplitAttachments(raw);
        if (tokens.Count == 0)
        {
            return new AttachmentInsights(false, 0, false, 0, string.Empty);
        }

        var imageCount = 0;
        var context = new StringBuilder();

        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();
            var extension = ExtractExtension(lower);
            var isImage = ImageExtensions.Contains(extension) ||
                          lower.Contains("screenshot", StringComparison.OrdinalIgnoreCase) ||
                          lower.Contains("скрин", StringComparison.OrdinalIgnoreCase) ||
                          lower.Contains("фото", StringComparison.OrdinalIgnoreCase) ||
                          lower.Contains("image", StringComparison.OrdinalIgnoreCase);

            if (isImage)
            {
                imageCount++;
            }

            if (lower.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                lower.Contains("ошиб", StringComparison.OrdinalIgnoreCase))
            {
                context.Append(" app error screenshot");
            }

            if (lower.Contains("fraud", StringComparison.OrdinalIgnoreCase) ||
                lower.Contains("мошен", StringComparison.OrdinalIgnoreCase))
            {
                context.Append(" fraud evidence");
            }
        }

        return new AttachmentInsights(
            HasAttachments: true,
            AttachmentCount: tokens.Count,
            HasImageAttachment: imageCount > 0,
            ImageAttachmentCount: imageCount,
            ContextForNlp: context.ToString().Trim()
        );
    }

    private static List<string> SplitAttachments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var normalized = raw.Replace('\r', '\n');
        var parts = normalized
            .Split([';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        return parts;
    }

    private static string ExtractExtension(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return Path.GetExtension(uri.AbsolutePath);
        }

        return Path.GetExtension(value);
    }
}
