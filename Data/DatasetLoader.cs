using KhajikiSort.Models;

namespace KhajikiSort.Data;

public static class DatasetLoader
{
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
}
