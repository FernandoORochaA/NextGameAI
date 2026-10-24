namespace NextGameAI.Web.Services.Igdb;

// Immutable application contract, independent of both EF entities and the external JSON schema.
public sealed record IgdbGameResult(
    long IgdbId,
    string Title,
    string? CoverImageId,
    DateOnly? FirstReleaseDate,
    long? GameTypeId,
    string? GameType,
    string? EditionTitle,
    long? VersionParentIgdbId)
{
    public int? ReleaseYear => FirstReleaseDate?.Year;
}

public enum IgdbCoverSize { Small, Big }

public static class IgdbCoverImages
{
    internal static bool IsValidId(string? imageId) => imageId is { Length: > 0 and <= 128 } &&
        imageId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static Uri? GetUrl(string? imageId, IgdbCoverSize size = IgdbCoverSize.Small)
    {
        if (!IsValidId(imageId)) return null;
        var format = size switch
        {
            IgdbCoverSize.Small => "cover_small",
            IgdbCoverSize.Big => "cover_big",
            _ => throw new ArgumentOutOfRangeException(nameof(size))
        };
        return new Uri($"https://images.igdb.com/igdb/image/upload/t_{format}/{imageId}.jpg");
    }
}
