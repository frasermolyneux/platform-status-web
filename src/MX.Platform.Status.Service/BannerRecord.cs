using MX.Platform.Status.App.Contracts;

namespace MX.Platform.Status.Service;

public sealed record BannerRecord
{
    public int Id { get; init; }
    public string SiteId { get; init; } = "";
    public int Revision { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Tone { get; init; } = "info";
    public bool Published { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public BannerDto ToPublicBanner() => new()
    {
        Id = Id, Title = Title, Message = Message, Tone = Tone,
        StartsAt = StartsAt, ExpiresAt = ExpiresAt, UpdatedAt = UpdatedAt
    };
}

public sealed record BannerCommand(
    string Title, string Message, string Tone, bool Published, int Revision = 0,
    DateTimeOffset? StartsAt = null, DateTimeOffset? ExpiresAt = null)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 120
            || string.IsNullOrWhiteSpace(Message) || Message.Length > 2000)
            return "A title (up to 120 characters) and message (up to 2000 characters) are required.";
        if (Tone is not ("info" or "warning" or "critical"))
            return "Choose an informational, warning or critical banner.";
        if (Revision < 0 || ExpiresAt <= StartsAt)
            return "The expiry must be after the start and the revision must be valid.";
        if (Published && ExpiresAt <= DateTimeOffset.UtcNow)
            return "An expired banner cannot be published. Choose a future expiry or leave it blank.";
        return null;
    }

    public BannerRecord Apply(int id, string site, BannerRecord? previous, DateTimeOffset now) => new()
    {
        Id = id, SiteId = site, Revision = (previous?.Revision ?? 0) + 1,
        Title = Title.Trim(), Message = Message.Trim(), Tone = Tone,
        Published = Published, StartsAt = StartsAt, ExpiresAt = ExpiresAt, UpdatedAt = now
    };
}
