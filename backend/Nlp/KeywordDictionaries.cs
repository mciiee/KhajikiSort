using System.Text.Json;
using KhajikiSort.Models;

namespace KhajikiSort.Nlp;

internal static class KeywordDictionaries
{
    public static Dictionary<LanguageCode, HashSet<string>> LanguageMarkers { get; }
    public static Dictionary<LanguageCode, Dictionary<RequestType, string[]>> RequestTypeKeywords { get; }
    public static Dictionary<LanguageCode, string[]> PositiveToneKeywords { get; }
    public static Dictionary<LanguageCode, string[]> NegativeToneKeywords { get; }
    public static HashSet<string> KazakhLatinSignals { get; }
    public static HashSet<string> RussianLatinSignals { get; }
    public static HashSet<char> KazakhSpecificLetters { get; }

    static KeywordDictionaries()
    {
        LanguageMarkers = new Dictionary<LanguageCode, HashSet<string>>();
        RequestTypeKeywords = new Dictionary<LanguageCode, Dictionary<RequestType, string[]>>();
        PositiveToneKeywords = new Dictionary<LanguageCode, string[]>();
        NegativeToneKeywords = new Dictionary<LanguageCode, string[]>();

        LoadLanguageFile(LanguageCode.RU, "ru.json");
        LoadLanguageFile(LanguageCode.KZ, "kz.json");
        LoadLanguageFile(LanguageCode.ENG, "eng.json");

        var signals = LoadJson<LanguageSignalsDto>("signals.json");
        KazakhLatinSignals = new HashSet<string>(signals.KazakhLatinSignals ?? [], StringComparer.OrdinalIgnoreCase);
        RussianLatinSignals = new HashSet<string>(signals.RussianLatinSignals ?? [], StringComparer.OrdinalIgnoreCase);
        KazakhSpecificLetters = new HashSet<char>((signals.KazakhSpecificLetters ?? []).SelectMany(x => x));
    }

    private static void LoadLanguageFile(LanguageCode language, string fileName)
    {
        var dto = LoadJson<LanguageDictionaryDto>(fileName);

        LanguageMarkers[language] = new HashSet<string>(
            dto.LanguageMarkers ?? [],
            StringComparer.OrdinalIgnoreCase);

        PositiveToneKeywords[language] = dto.PositiveTone ?? [];
        NegativeToneKeywords[language] = dto.NegativeTone ?? [];

        var requestMap = new Dictionary<RequestType, string[]>();
        if (dto.RequestTypes is not null)
        {
            foreach (var (typeName, keywords) in dto.RequestTypes)
            {
                if (Enum.TryParse<RequestType>(typeName, true, out var requestType))
                {
                    requestMap[requestType] = keywords ?? [];
                }
            }
        }

        foreach (var requestType in Enum.GetValues<RequestType>())
        {
            if (!requestMap.ContainsKey(requestType))
            {
                requestMap[requestType] = [];
            }
        }

        RequestTypeKeywords[language] = requestMap;
    }

    private static T LoadJson<T>(string fileName)
    {
        var path = Path.Combine(ResolveDictionariesDirectory(), fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Dictionary file not found: {path}");
        }

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (dto is null)
        {
            throw new InvalidOperationException($"Failed to deserialize dictionary file: {path}");
        }

        return dto;
    }

    private static string ResolveDictionariesDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidatePaths = new[]
        {
            Path.Combine(baseDir, "Dictionaries"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "dictionaries")),
            Path.Combine(baseDir, "Nlp", "Dictionaries"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Nlp", "Dictionaries"))
        };

        foreach (var candidate in candidatePaths)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Nlp dictionaries directory.");
    }

    private sealed class LanguageDictionaryDto
    {
        public string[]? LanguageMarkers { get; init; }
        public Dictionary<string, string[]?>? RequestTypes { get; init; }
        public string[]? PositiveTone { get; init; }
        public string[]? NegativeTone { get; init; }
    }

    private sealed class LanguageSignalsDto
    {
        public string[]? KazakhLatinSignals { get; init; }
        public string[]? RussianLatinSignals { get; init; }
        public string[]? KazakhSpecificLetters { get; init; }
    }
}
