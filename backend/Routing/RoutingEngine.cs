using KhajikiSort.Models;

namespace KhajikiSort.Routing;

public sealed class RoutingEngine
{
    private readonly Dictionary<string, int> _roundRobinCounters = new(StringComparer.OrdinalIgnoreCase);
    private bool _fallbackToggle;
    private static readonly Dictionary<string, (double Lat, double Lon)> CityCoordinates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Актау"] = (43.6532, 51.1975),
        ["Актобе"] = (50.2839, 57.1669),
        ["Алматы"] = (43.2389, 76.8897),
        ["Астана"] = (51.1694, 71.4491),
        ["Атырау"] = (47.0945, 51.9238),
        ["Караганда"] = (49.8060, 73.0850),
        ["Кокшетау"] = (53.2833, 69.3833),
        ["Костанай"] = (53.2144, 63.6246),
        ["Кызылорда"] = (44.8488, 65.4823),
        ["Павлодар"] = (52.2871, 76.9733),
        ["Петропавловск"] = (54.8728, 69.1430),
        ["Тараз"] = (42.9004, 71.3655),
        ["Уральск"] = (51.2300, 51.3670),
        ["Усть-Каменогорск"] = (49.9483, 82.6275),
        ["Шымкент"] = (42.3417, 69.5901)
    };

    public ProcessedTicket Route(
        Ticket ticket,
        AiMetadata aiMetadata,
        AttachmentInsights attachmentInsights,
        List<Manager> managers,
        List<BusinessUnit> businessUnits)
    {
        var office = SelectOffice(ticket, managers, businessUnits);
        var candidates = FilterCandidates(ticket, aiMetadata, managers, office);
        var assignmentReason = "Assigned by office filter + hard skills + round robin.";

        if (candidates.Count == 0)
        {
            var nearestOffice = FindNearestOfficeWithCandidates(ticket, aiMetadata, managers, businessUnits, office);
            if (!string.IsNullOrWhiteSpace(nearestOffice))
            {
                office = nearestOffice;
                candidates = FilterCandidates(ticket, aiMetadata, managers, office);
                assignmentReason = "Assigned by nearest-city fallback + hard skills + round robin.";
            }
        }

        if (candidates.Count == 0)
        {
            return new ProcessedTicket(
                Ticket: ticket,
                AttachmentInsights: attachmentInsights,
                AiMetadata: aiMetadata,
                SelectedOffice: office,
                SelectedManager: null,
                AssignmentReason: "No managers matched hard-skill filters."
            );
        }

        var topTwo = candidates
            .OrderBy(m => m.CurrentLoad)
            .ThenBy(m => m.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        var selectedManager = SelectByRoundRobin(office, topTwo);
        selectedManager.CurrentLoad += 1;

        return new ProcessedTicket(
            Ticket: ticket,
            AttachmentInsights: attachmentInsights,
            AiMetadata: aiMetadata,
            SelectedOffice: office,
            SelectedManager: selectedManager.FullName,
            AssignmentReason: $"{assignmentReason} CandidatePool={candidates.Count}."
        );
    }

    private string SelectOffice(Ticket ticket, List<Manager> managers, List<BusinessUnit> units)
    {
        if (Needs5050Fallback(ticket))
        {
            return NextFallbackOffice(units);
        }

        var offices = units.Select(u => u.Office).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (offices.Contains(ticket.Settlement))
        {
            return ticket.Settlement;
        }

        var byRegion = MapRegionToOffice(ticket.Region);
        if (byRegion is not null && offices.Contains(byRegion))
        {
            return byRegion;
        }

        var leastLoadedOffice = managers
            .GroupBy(m => m.Office)
            .OrderBy(g => g.Sum(m => m.CurrentLoad))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(leastLoadedOffice) ? NextFallbackOffice(units) : leastLoadedOffice;
    }

    private static List<Manager> FilterCandidates(Ticket ticket, AiMetadata aiMetadata, List<Manager> managers, string office)
    {
        var query = managers.Where(m => string.Equals(m.Office, office, StringComparison.OrdinalIgnoreCase));

        if (IsVipOrPriority(ticket.Segment))
        {
            query = query.Where(m => m.Skills.Contains("VIP"));
        }

        if (aiMetadata.RequestType == RequestType.DataChange)
        {
            query = query.Where(m => IsChiefSpecialist(m.Position));
        }

        if (aiMetadata.Language == LanguageCode.KZ)
        {
            query = query.Where(m => m.Skills.Contains("KZ"));
        }
        else if (aiMetadata.Language == LanguageCode.ENG)
        {
            query = query.Where(m => m.Skills.Contains("ENG"));
        }

        return query.ToList();
    }

    private Manager SelectByRoundRobin(string office, List<Manager> topTwo)
    {
        if (topTwo.Count == 1)
        {
            return topTwo[0];
        }

        _roundRobinCounters.TryGetValue(office, out var index);
        var selected = topTwo[index % 2];
        _roundRobinCounters[office] = index + 1;
        return selected;
    }

    private static bool Needs5050Fallback(Ticket ticket)
    {
        var hasUnknownAddress = string.IsNullOrWhiteSpace(ticket.Country) ||
                                string.IsNullOrWhiteSpace(ticket.Region) ||
                                string.IsNullOrWhiteSpace(ticket.Settlement);

        var isForeign = !string.IsNullOrWhiteSpace(ticket.Country) &&
                        !ticket.Country.Contains("казахстан", StringComparison.OrdinalIgnoreCase);

        return hasUnknownAddress || isForeign;
    }

    private string NextFallbackOffice(List<BusinessUnit> units)
    {
        var offices = units.Select(u => u.Office).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var astana = offices.Contains("Астана") ? "Астана" : units.FirstOrDefault()?.Office ?? "Астана";
        var almaty = offices.Contains("Алматы") ? "Алматы" : units.Skip(1).FirstOrDefault()?.Office ?? astana;

        _fallbackToggle = !_fallbackToggle;
        return _fallbackToggle ? astana : almaty;
    }

    private static bool IsVipOrPriority(string segment)
    {
        return segment.Contains("vip", StringComparison.OrdinalIgnoreCase) ||
               segment.Contains("priority", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChiefSpecialist(string position)
    {
        return position.Contains("главный", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MapRegionToOffice(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return null;
        }

        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["алмат"] = "Алматы",
            ["акмол"] = "Астана",
            ["кызылорд"] = "Кызылорда",
            ["атырау"] = "Атырау",
            ["караг"] = "Караганда",
            ["костан"] = "Костанай",
            ["павлодар"] = "Павлодар",
            ["актюб"] = "Актобе",
            ["актоб"] = "Актобе",
            ["западно"] = "Уральск",
            ["восточно"] = "Усть-Каменогорск",
            ["жамбыл"] = "Тараз",
            ["северо"] = "Петропавловск",
            ["мангист"] = "Актау",
            ["туркест"] = "Шымкент"
        };

        foreach (var (needle, office) in mappings)
        {
            if (region.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return office;
            }
        }

        return null;
    }

    private string? FindNearestOfficeWithCandidates(
        Ticket ticket,
        AiMetadata aiMetadata,
        List<Manager> managers,
        List<BusinessUnit> businessUnits,
        string primaryOffice)
    {
        var offices = businessUnits.Select(x => x.Office).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var officeCandidates = offices
            .Select(office => new
            {
                Office = office,
                Candidates = FilterCandidates(ticket, aiMetadata, managers, office)
            })
            .Where(x => x.Candidates.Count > 0)
            .ToList();

        if (officeCandidates.Count == 0)
        {
            return null;
        }

        var originCity = ResolveOriginCity(ticket, primaryOffice);
        var withDistance = officeCandidates
            .Select(x => new
            {
                x.Office,
                Distance = DistanceBetweenCities(originCity, x.Office),
                Load = x.Candidates.Min(c => c.CurrentLoad)
            })
            .OrderBy(x => x.Distance ?? double.MaxValue)
            .ThenBy(x => x.Load)
            .ThenBy(x => x.Office, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return withDistance?.Office;
    }

    private static string ResolveOriginCity(Ticket ticket, string primaryOffice)
    {
        if (!string.IsNullOrWhiteSpace(ticket.Settlement))
        {
            return ticket.Settlement;
        }

        var byRegion = MapRegionToOffice(ticket.Region);
        if (!string.IsNullOrWhiteSpace(byRegion))
        {
            return byRegion;
        }

        return primaryOffice;
    }

    private static double? DistanceBetweenCities(string from, string to)
    {
        if (!CityCoordinates.TryGetValue(from, out var a) ||
            !CityCoordinates.TryGetValue(to, out var b))
        {
            return null;
        }

        return HaversineKm(a.Lat, a.Lon, b.Lat, b.Lon);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusKm = 6371.0;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(DegreesToRadians(lat1)) *
                Math.Cos(DegreesToRadians(lat2)) *
                Math.Pow(Math.Sin(dLon / 2), 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }
}
