using System.Text;
using KhajikiSort.Models;

namespace KhajikiSort.Data;

public static class RoutingResultWriter
{
    public static void Write(string path, IReadOnlyCollection<ProcessedTicket> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ClientId,Segment,Language,RequestType,Tone,Priority,HasImageAttachment,SelectedOffice,SelectedManager,AssignmentReason,Summary");

        foreach (var row in rows)
        {
            var values = new[]
            {
                row.Ticket.ClientId,
                row.Ticket.Segment,
                row.AiMetadata.Language.ToString(),
                row.AiMetadata.RequestType.ToString(),
                row.AiMetadata.Tone.ToString(),
                row.AiMetadata.Priority.ToString(),
                row.AttachmentInsights.HasImageAttachment.ToString(),
                row.SelectedOffice,
                row.SelectedManager ?? "UNASSIGNED",
                row.AssignmentReason,
                row.AiMetadata.Summary
            };

            sb.AppendLine(string.Join(',', values.Select(Escape)));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
