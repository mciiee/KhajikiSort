using System.Text;

namespace KhajikiSort.Data;

public static class CsvTableReader
{
    public static List<Dictionary<string, string>> ReadRows(string path)
    {
        var table = Parse(path);
        if (table.Count == 0)
        {
            return [];
        }

        var headers = table[0];
        var rows = new List<Dictionary<string, string>>();

        for (var rowIndex = 1; rowIndex < table.Count; rowIndex++)
        {
            var row = table[rowIndex];
            var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < headers.Count; i++)
            {
                var header = NormalizeHeader(headers[i]);
                var value = i < row.Count ? row[i].Trim() : string.Empty;
                mapped[header] = value;
            }

            rows.Add(mapped);
        }

        return rows;
    }

    private static List<List<string>> Parse(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var table = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    var isEscapedQuote = i + 1 < text.Length && text[i + 1] == '"';
                    if (isEscapedQuote)
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                continue;
            }

            if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                table.Add(row);
                row = new List<string>();
                continue;
            }

            field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            table.Add(row);
        }

        return table;
    }

    public static string NormalizeHeader(string header)
    {
        var normalized = header
            .Trim()
            .ToLowerInvariant()
            .Replace("ё", "е")
            .Replace(" ", string.Empty);

        return normalized;
    }
}
