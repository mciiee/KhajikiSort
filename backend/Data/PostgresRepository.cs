using System.Text.Json;
using KhajikiSort.Models;
using Npgsql;

namespace KhajikiSort.Data;

public static class PostgresRepository
{
    public static async Task EnsureSchemaAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
    }

    public static async Task<int> GetSourceTicketCountAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        const string sql = "select count(*) from fire.source_tickets;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value);
    }

    public static async Task SeedSourceDataAsync(
        string connectionString,
        IReadOnlyCollection<Ticket> tickets,
        IReadOnlyCollection<Manager> managers,
        IReadOnlyCollection<BusinessUnit> businessUnits,
        string dictionariesDir,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await UpsertSourceTicketsAsync(conn, tickets, ct);
        await UpsertSourceManagersAsync(conn, managers, ct);
        await UpsertBusinessUnitsAsync(conn, businessUnits, ct);
        await UpsertDictionaryTermsAsync(conn, dictionariesDir, ct);
        await tx.CommitAsync(ct);
    }

    public static async Task<List<Ticket>> LoadTicketsAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        const string sql = """
            select ticket_id, gender, birth_date, description, attachments_raw, segment, country, region, settlement, street, house
            from fire.source_tickets
            order by imported_at, ticket_id;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Ticket>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Ticket(
                ClientId: reader.GetString(0),
                Gender: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                BirthDate: reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                Description: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                AttachmentsRaw: reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Segment: reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Country: reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                Region: reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                Settlement: reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                Street: reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                House: reader.IsDBNull(10) ? string.Empty : reader.GetString(10)
            ));
        }

        return rows;
    }

    public static async Task<List<Manager>> LoadManagersAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        const string sql = """
            select full_name, position, office, skills, current_load
            from fire.source_managers
            order by full_name;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Manager>();
        while (await reader.ReadAsync(ct))
        {
            var skillsArray = reader.IsDBNull(3) ? [] : reader.GetFieldValue<string[]>(3);
            rows.Add(new Manager
            {
                FullName = reader.GetString(0),
                Position = reader.GetString(1),
                Office = reader.GetString(2),
                Skills = skillsArray.ToHashSet(StringComparer.OrdinalIgnoreCase),
                CurrentLoad = reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
            });
        }

        return rows;
    }

    public static async Task<List<BusinessUnit>> LoadBusinessUnitsAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        const string sql = """
            select office, address
            from fire.business_units
            order by office;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<BusinessUnit>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BusinessUnit(
                Office: reader.GetString(0),
                Address: reader.GetString(1)
            ));
        }

        return rows;
    }

    public static async Task UpsertRoutedTicketsAsync(
        string connectionString,
        IReadOnlyCollection<ProcessedTicket> routedTickets,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        const string sql = """
            insert into fire.routed_tickets
              (ticket_id, request_type, tone, priority, language, summary, recommendation, manager_summary, image_analysis, analysis_source, has_attachments,
               attachment_count, has_image_attachment, image_attachment_count, selected_office, selected_manager, assignment_reason)
            values
              (@ticket_id, @request_type, @tone, @priority, @language, @summary, @recommendation, @manager_summary, @image_analysis, @analysis_source, @has_attachments,
               @attachment_count, @has_image_attachment, @image_attachment_count, @selected_office, @selected_manager, @assignment_reason)
            on conflict (ticket_id) do update
              set request_type = excluded.request_type,
                  tone = excluded.tone,
                  priority = excluded.priority,
                  language = excluded.language,
                  summary = excluded.summary,
                  recommendation = excluded.recommendation,
                  manager_summary = excluded.manager_summary,
                  image_analysis = excluded.image_analysis,
                  analysis_source = excluded.analysis_source,
                  has_attachments = excluded.has_attachments,
                  attachment_count = excluded.attachment_count,
                  has_image_attachment = excluded.has_image_attachment,
                  image_attachment_count = excluded.image_attachment_count,
                  selected_office = excluded.selected_office,
                  selected_manager = excluded.selected_manager,
                  assignment_reason = excluded.assignment_reason,
                  exported_at = now();
            """;

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var row in routedTickets)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("ticket_id", row.Ticket.ClientId);
            cmd.Parameters.AddWithValue("request_type", row.AiMetadata.RequestType.ToString());
            cmd.Parameters.AddWithValue("tone", row.AiMetadata.Tone.ToString());
            cmd.Parameters.AddWithValue("priority", row.AiMetadata.Priority);
            cmd.Parameters.AddWithValue("language", row.AiMetadata.Language.ToString());
            cmd.Parameters.AddWithValue("summary", row.AiMetadata.Summary);
            cmd.Parameters.AddWithValue("recommendation", row.AiMetadata.Recommendation);
            cmd.Parameters.AddWithValue("manager_summary", ManagerSummaryFormatter.Build(row.AiMetadata.Summary, row.AiMetadata.Recommendation));
            cmd.Parameters.AddWithValue("image_analysis", row.AiMetadata.ImageAnalysis ?? string.Empty);
            cmd.Parameters.AddWithValue("analysis_source", row.AiMetadata.AnalysisSource ?? string.Empty);
            cmd.Parameters.AddWithValue("has_attachments", row.AttachmentInsights.HasAttachments);
            cmd.Parameters.AddWithValue("attachment_count", row.AttachmentInsights.AttachmentCount);
            cmd.Parameters.AddWithValue("has_image_attachment", row.AttachmentInsights.HasImageAttachment);
            cmd.Parameters.AddWithValue("image_attachment_count", row.AttachmentInsights.ImageAttachmentCount);
            cmd.Parameters.AddWithValue("selected_office", row.SelectedOffice);
            cmd.Parameters.AddWithValue("selected_manager", (object?)row.SelectedManager ?? DBNull.Value);
            cmd.Parameters.AddWithValue("assignment_reason", row.AssignmentReason);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task EnsureSchemaAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            create schema if not exists fire;

            create table if not exists fire.source_tickets (
              ticket_id text primary key,
              gender text,
              birth_date timestamp null,
              description text,
              attachments_raw text,
              segment text,
              country text,
              region text,
              settlement text,
              street text,
              house text,
              imported_at timestamptz not null default now()
            );

            create table if not exists fire.source_managers (
              full_name text primary key,
              position text not null,
              office text not null,
              skills text[] not null,
              current_load int not null,
              imported_at timestamptz not null default now()
            );

            create table if not exists fire.business_units (
              office text primary key,
              address text not null,
              imported_at timestamptz not null default now()
            );

            create table if not exists fire.routed_tickets (
              ticket_id text primary key references fire.source_tickets(ticket_id),
              request_type text not null,
              tone text not null,
              priority int not null,
              language text not null,
              summary text not null,
              recommendation text not null,
              manager_summary text not null default '',
              image_analysis text not null default '',
              analysis_source text not null default '',
              has_attachments boolean not null,
              attachment_count int not null,
              has_image_attachment boolean not null,
              image_attachment_count int not null,
              selected_office text not null,
              selected_manager text null,
              assignment_reason text not null,
              exported_at timestamptz not null default now()
            );

            create table if not exists fire.dictionary_terms (
              id bigserial primary key,
              language_code text not null,
              dictionary_group text not null,
              request_type text not null default '',
              term text not null,
              imported_at timestamptz not null default now(),
              unique(language_code, dictionary_group, request_type, term)
            );
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertSourceTicketsAsync(NpgsqlConnection conn, IEnumerable<Ticket> tickets, CancellationToken ct)
    {
        const string sql = """
            insert into fire.source_tickets
              (ticket_id, gender, birth_date, description, attachments_raw, segment, country, region, settlement, street, house)
            values
              (@ticket_id, @gender, @birth_date, @description, @attachments_raw, @segment, @country, @region, @settlement, @street, @house)
            on conflict (ticket_id) do update
              set gender = excluded.gender,
                  birth_date = excluded.birth_date,
                  description = excluded.description,
                  attachments_raw = excluded.attachments_raw,
                  segment = excluded.segment,
                  country = excluded.country,
                  region = excluded.region,
                  settlement = excluded.settlement,
                  street = excluded.street,
                  house = excluded.house,
                  imported_at = now();
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var t in tickets)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("ticket_id", t.ClientId);
            cmd.Parameters.AddWithValue("gender", Db(t.Gender));
            cmd.Parameters.AddWithValue("birth_date", (object?)t.BirthDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("description", Db(t.Description));
            cmd.Parameters.AddWithValue("attachments_raw", Db(t.AttachmentsRaw));
            cmd.Parameters.AddWithValue("segment", Db(t.Segment));
            cmd.Parameters.AddWithValue("country", Db(t.Country));
            cmd.Parameters.AddWithValue("region", Db(t.Region));
            cmd.Parameters.AddWithValue("settlement", Db(t.Settlement));
            cmd.Parameters.AddWithValue("street", Db(t.Street));
            cmd.Parameters.AddWithValue("house", Db(t.House));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpsertSourceManagersAsync(NpgsqlConnection conn, IEnumerable<Manager> managers, CancellationToken ct)
    {
        const string sql = """
            insert into fire.source_managers
              (full_name, position, office, skills, current_load)
            values
              (@full_name, @position, @office, @skills, @current_load)
            on conflict (full_name) do update
              set position = excluded.position,
                  office = excluded.office,
                  skills = excluded.skills,
                  current_load = excluded.current_load,
                  imported_at = now();
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var m in managers)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("full_name", m.FullName);
            cmd.Parameters.AddWithValue("position", m.Position);
            cmd.Parameters.AddWithValue("office", m.Office);
            cmd.Parameters.AddWithValue("skills", m.Skills.OrderBy(x => x).ToArray());
            cmd.Parameters.AddWithValue("current_load", m.CurrentLoad);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpsertBusinessUnitsAsync(NpgsqlConnection conn, IEnumerable<BusinessUnit> units, CancellationToken ct)
    {
        const string sql = """
            insert into fire.business_units
              (office, address)
            values
              (@office, @address)
            on conflict (office) do update
              set address = excluded.address,
                  imported_at = now();
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var unit in units)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("office", unit.Office);
            cmd.Parameters.AddWithValue("address", unit.Address);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpsertDictionaryTermsAsync(NpgsqlConnection conn, string dictionariesDir, CancellationToken ct)
    {
        var rows = BuildDictionaryRows(dictionariesDir);
        const string sql = """
            insert into fire.dictionary_terms
              (language_code, dictionary_group, request_type, term)
            values
              (@language_code, @dictionary_group, @request_type, @term)
            on conflict do nothing;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var row in rows)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("language_code", row.LanguageCode);
            cmd.Parameters.AddWithValue("dictionary_group", row.DictionaryGroup);
            cmd.Parameters.AddWithValue("request_type", row.RequestType ?? string.Empty);
            cmd.Parameters.AddWithValue("term", row.Term);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static List<DictionaryRow> BuildDictionaryRows(string dictionariesDir)
    {
        var list = new List<DictionaryRow>();
        foreach (var lang in new[] { "ru", "kz", "eng" })
        {
            var path = Path.Combine(dictionariesDir, $"{lang}.json");
            var dto = Deserialize<LanguageDictionaryDto>(path);

            foreach (var marker in dto.LanguageMarkers ?? [])
            {
                list.Add(new DictionaryRow(lang.ToUpperInvariant(), "language_marker", null, marker));
            }

            foreach (var tone in dto.PositiveTone ?? [])
            {
                list.Add(new DictionaryRow(lang.ToUpperInvariant(), "tone_positive", null, tone));
            }

            foreach (var tone in dto.NegativeTone ?? [])
            {
                list.Add(new DictionaryRow(lang.ToUpperInvariant(), "tone_negative", null, tone));
            }

            if (dto.RequestTypes is not null)
            {
                foreach (var (requestType, terms) in dto.RequestTypes)
                {
                    foreach (var term in terms ?? [])
                    {
                        list.Add(new DictionaryRow(lang.ToUpperInvariant(), "request_type", requestType, term));
                    }
                }
            }
        }

        var signals = Deserialize<SignalsDictionaryDto>(Path.Combine(dictionariesDir, "signals.json"));
        foreach (var term in signals.KazakhLatinSignals ?? [])
        {
            list.Add(new DictionaryRow("KZ", "signal_kz_latin", null, term));
        }

        foreach (var term in signals.RussianLatinSignals ?? [])
        {
            list.Add(new DictionaryRow("RU", "signal_ru_latin", null, term));
        }

        foreach (var term in signals.KazakhSpecificLetters ?? [])
        {
            list.Add(new DictionaryRow("KZ", "signal_kz_char", null, term));
        }

        return list;
    }

    private static T Deserialize<T>(string path)
    {
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
            throw new InvalidOperationException($"Failed to parse dictionary file: {path}");
        }

        return dto;
    }

    private sealed record DictionaryRow(string LanguageCode, string DictionaryGroup, string? RequestType, string Term);

    private sealed class LanguageDictionaryDto
    {
        public string[]? LanguageMarkers { get; init; }
        public Dictionary<string, string[]?>? RequestTypes { get; init; }
        public string[]? PositiveTone { get; init; }
        public string[]? NegativeTone { get; init; }
    }

    private sealed class SignalsDictionaryDto
    {
        public string[]? KazakhLatinSignals { get; init; }
        public string[]? RussianLatinSignals { get; init; }
        public string[]? KazakhSpecificLetters { get; init; }
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
