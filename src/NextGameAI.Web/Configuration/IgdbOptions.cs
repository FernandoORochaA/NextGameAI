namespace NextGameAI.Web.Configuration;

public sealed class IgdbOptions
{
    public const string SectionName = "Igdb";
    public bool Enabled { get; set; } = true;
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
}
