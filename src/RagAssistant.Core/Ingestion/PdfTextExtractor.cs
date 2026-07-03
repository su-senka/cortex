using System.Text;
using UglyToad.PdfPig;

namespace RagAssistant.Core.Ingestion;

/// <summary>
/// Converts a PDF into markdown-ish text so it can flow through the same
/// section-splitting chunker as .md files: each page becomes an ATX heading
/// ("## Page N"), which keeps page numbers in the chunk breadcrumbs and lets
/// citations point users to the right page.
/// </summary>
public static class PdfTextExtractor
{
    public static string ExtractAsMarkdown(string path)
    {
        var sb = new StringBuilder();
        using var document = PdfDocument.Open(path);

        foreach (var page in document.GetPages())
        {
            var text = page.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;

            sb.Append("## Page ").Append(page.Number).AppendLine();
            sb.AppendLine(text.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
