using KhajikiSort.Models;

namespace KhajikiSort.Data;

public static class DatasetLoader
{
    public static List<Ticket> LoadTicketsFromMany(params string[] paths)
    {
        var merged = new List<Ticket>();
        var idCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ExistingPaths(paths))
        {
            foreach (var row in LoadTickets(path))
            {
                var ticket = row;
                if (string.IsNullOrWhiteSpace(ticket.ClientId))
                {
                    continue;
                }

                if (!idCounters.TryAdd(ticket.ClientId, 1))
                {
                    idCounters[ticket.ClientId]++;
                    var duplicateNo = idCounters[ticket.ClientId];
                    ticket = ticket with { ClientId = $"{ticket.ClientId}__dup{duplicateNo}" };
                }

                merged.Add(ticket);
            }
        }

        return merged;
    }

    public static List<Manager> LoadManagersFromMany(params string[] paths)
    {
        var merged = new Dictionary<string, Manager>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ExistingPaths(paths))
        {
            foreach (var row in LoadManagers(path))
            {
                if (string.IsNullOrWhiteSpace(row.FullName))
                {
                    continue;
                }

                merged[row.FullName] = row;
            }
        }

        return merged.Values.ToList();
    }

    public static List<BusinessUnit> LoadBusinessUnitsFromMany(params string[] paths)
    {
        var merged = new Dictionary<string, BusinessUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ExistingPaths(paths))
        {
            foreach (var row in LoadBusinessUnits(path))
            {
                if (string.IsNullOrWhiteSpace(row.Office))
                {
                    continue;
                }

                merged[row.Office] = row;
            }
        }

        return merged.Values.ToList();
    }

    public static List<Ticket> LoadTickets(string path)
    {
        return CsvTableReader.ReadRows(path).Select(row => new Ticket(
            ClientId: Get(row, "guidклиента"),
            Gender: Get(row, "полклиента"),
            BirthDate: ParseDate(Get(row, "датарождения")),
            Description: Get(row, "описание"),
            AttachmentsRaw: Get(row, "вложения"),
            Segment: Get(row, "сегментклиента"),
            Country: Get(row, "страна"),
            Region: Get(row, "область"),
            Settlement: Get(row, "населенныйпункт", "населённыйпункт", "населеныйпункт"),
            Street: Get(row, "улица"),
            House: Get(row, "дом")
        )).ToList();
    }

    public static List<Manager> LoadManagers(string path)
    {
        return CsvTableReader.ReadRows(path).Select(row =>
        {
            var rawSkills = Get(row, "навыки");
            var skills = rawSkills
                .Split([','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(skill => skill.ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new Manager
            {
                FullName = Get(row, "фио"),
                Position = Get(row, "должность"),
                Office = Get(row, "офис"),
                Skills = skills,
                CurrentLoad = ParseInt(Get(row, "количествообращенийвработе"))
            };
        }).ToList();
    }

    public static List<BusinessUnit> LoadBusinessUnits(string path)
    {
        return CsvTableReader.ReadRows(path)
            .Select(row => new BusinessUnit(Get(row, "офис"), Get(row, "адрес")))
            .ToList();
    }

    private static string Get(Dictionary<string, string> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(CsvTableReader.NormalizeHeader(key), out var value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static DateTime? ParseDate(string raw)
    {
        return DateTime.TryParse(raw, out var date) ? date : null;
    }

    private static int ParseInt(string raw)
    {
        return int.TryParse(raw, out var value) ? value : 0;
    }

    private static IEnumerable<string> ExistingPaths(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var full = Path.GetFullPath(path);
            if (!seen.Add(full))
            {
                continue;
            }

            if (File.Exists(full))
            {
                yield return full;
            }
        }
    }
}
