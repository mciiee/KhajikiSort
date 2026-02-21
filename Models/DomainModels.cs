namespace KhajikiSort.Models;

public sealed record Ticket(
    string ClientId,
    string Gender,
    DateTime? BirthDate,
    string Description,
    string AttachmentsRaw,
    string Segment,
    string Country,
    string Region,
    string Settlement,
    string Street,
    string House
);

public sealed class Manager
{
    public required string FullName { get; init; }
    public required string Position { get; init; }
    public required string Office { get; init; }
    public required HashSet<string> Skills { get; init; }
    public int CurrentLoad { get; set; }
}

public sealed record BusinessUnit(
    string Office,
    string Address
);

public sealed record AttachmentInsights(
    bool HasAttachments,
    int AttachmentCount,
    bool HasImageAttachment,
    int ImageAttachmentCount,
    string ContextForNlp
);

public sealed record ProcessedTicket(
    Ticket Ticket,
    AttachmentInsights AttachmentInsights,
    AiMetadata AiMetadata,
    string SelectedOffice,
    string? SelectedManager,
    string AssignmentReason
);
