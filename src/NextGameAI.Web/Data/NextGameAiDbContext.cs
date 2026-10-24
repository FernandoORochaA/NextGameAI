using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Models;

namespace NextGameAI.Web.Data;

public class NextGameAiDbContext(DbContextOptions<NextGameAiDbContext> options) : DbContext(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<LibraryEntry> LibraryEntries => Set<LibraryEntry>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Platform> Platforms => Set<Platform>();
    public DbSet<GameOwnership> GameOwnerships => Set<GameOwnership>();
    public DbSet<CatalogTaxonomy> CatalogTaxonomies => Set<CatalogTaxonomy>();
    public DbSet<GameTaxonomy> GameTaxonomies => Set<GameTaxonomy>();
    public DbSet<GameTimeToBeat> GameTimeToBeats => Set<GameTimeToBeat>();
    public DbSet<CatalogPlatform> CatalogPlatforms => Set<CatalogPlatform>();
    public DbSet<GameMultiplayerMode> GameMultiplayerModes => Set<GameMultiplayerMode>();
    public DbSet<GameEnrichmentState> GameEnrichmentStates => Set<GameEnrichmentState>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var taxonomy = model.Entity<CatalogTaxonomy>();
        taxonomy.HasIndex(x => new { x.Kind, x.IgdbId }).IsUnique();
        taxonomy.ToTable(t =>
        {
            t.HasCheckConstraint("CK_CatalogTaxonomy_Kind", "Kind IN (0,1,2,3,4)");
            t.HasCheckConstraint("CK_CatalogTaxonomy_IgdbId", "IgdbId > 0");
            t.HasCheckConstraint("CK_CatalogTaxonomy_Name", "length(trim(Name)) > 0");
        });
        var link = model.Entity<GameTaxonomy>();
        link.HasKey(x => new { x.GameId, x.CatalogTaxonomyId });
        link.HasOne(x => x.Game).WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
        link.HasOne(x => x.CatalogTaxonomy).WithMany().HasForeignKey(x => x.CatalogTaxonomyId).OnDelete(DeleteBehavior.Restrict);

        var time = model.Entity<GameTimeToBeat>();
        time.HasKey(x => x.GameId);
        time.HasOne(x => x.Game).WithOne().HasForeignKey<GameTimeToBeat>(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
        time.HasIndex(x => x.IgdbId).IsUnique().HasFilter("IgdbId IS NOT NULL");
        time.ToTable(t =>
        {
            t.HasCheckConstraint("CK_GameTimeToBeat_Values", "(HastilySeconds IS NULL OR HastilySeconds >= 0) AND (NormallySeconds IS NULL OR NormallySeconds >= 0) AND (CompletelySeconds IS NULL OR CompletelySeconds >= 0) AND (SubmissionCount IS NULL OR SubmissionCount >= 0)");
            t.HasCheckConstraint("CK_GameTimeToBeat_IgdbId", "IgdbId IS NULL OR IgdbId > 0");
            t.HasCheckConstraint("CK_GameTimeToBeat_Source", "length(trim(Source)) > 0");
        });

        var catalogPlatform = model.Entity<CatalogPlatform>();
        catalogPlatform.HasIndex(x => x.IgdbId).IsUnique();
        catalogPlatform.ToTable(t =>
        {
            t.HasCheckConstraint("CK_CatalogPlatform_IgdbId", "IgdbId > 0");
            t.HasCheckConstraint("CK_CatalogPlatform_Name", "length(trim(Name)) > 0");
        });
        var multiplayer = model.Entity<GameMultiplayerMode>();
        multiplayer.HasIndex(x => x.IgdbId).IsUnique().HasFilter("IgdbId IS NOT NULL");
        multiplayer.HasIndex(x => new { x.GameId, x.CatalogPlatformId }).IsUnique();
        multiplayer.HasIndex(x => x.GameId).IsUnique().HasFilter("CatalogPlatformId IS NULL");
        multiplayer.HasOne(x => x.Game).WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
        multiplayer.HasOne(x => x.CatalogPlatform).WithMany().HasForeignKey(x => x.CatalogPlatformId).OnDelete(DeleteBehavior.Restrict);
        multiplayer.ToTable(t =>
        {
            t.HasCheckConstraint("CK_GameMultiplayerMode_IgdbId", "IgdbId IS NULL OR IgdbId > 0");
            t.HasCheckConstraint("CK_GameMultiplayerMode_Players", "(OfflineCoopMax IS NULL OR OfflineCoopMax >= 0) AND (OfflineMax IS NULL OR OfflineMax >= 0) AND (OnlineCoopMax IS NULL OR OnlineCoopMax >= 0) AND (OnlineMax IS NULL OR OnlineMax >= 0)");
            t.HasCheckConstraint("CK_GameMultiplayerMode_Source", "length(trim(Source)) > 0");
        });

        var state = model.Entity<GameEnrichmentState>();
        state.HasKey(x => new { x.GameId, x.Section });
        state.HasOne(x => x.Game).WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
        state.ToTable(t =>
        {
            t.HasCheckConstraint("CK_GameEnrichmentState_Section", "Section IN (0,1,2,3,4,5,6)");
            t.HasCheckConstraint("CK_GameEnrichmentState_Result", "LastResult IN (0,1,2)");
            t.HasCheckConstraint("CK_GameEnrichmentState_Timestamps", "(LastSuccessAtUtc IS NULL OR LastSuccessAtUtc <= LastAttemptAtUtc) AND (LastResult <> 0 OR LastSuccessAtUtc = LastAttemptAtUtc AND LastSuccessAtUtc IS NOT NULL)");
            t.HasCheckConstraint("CK_GameEnrichmentState_Source", "length(trim(Source)) > 0");
        });

        model.Entity<Game>().HasIndex(x => x.IgdbId).IsUnique().HasFilter("IgdbId IS NOT NULL");
        model.Entity<Platform>().HasIndex(x => x.IgdbId).IsUnique().HasFilter("IgdbId IS NOT NULL");
        model.Entity<Store>().HasIndex(x => x.Code).IsUnique();
        model.Entity<Platform>().HasIndex(x => x.Code).IsUnique();

        var entry = model.Entity<LibraryEntry>();
        entry.HasIndex(x => new { x.UserProfileId, x.GameId }).IsUnique();
        entry.HasIndex(x => new { x.UserProfileId, x.Status });
        entry.HasOne(x => x.UserProfile).WithMany(x => x.LibraryEntries)
            .HasForeignKey(x => x.UserProfileId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne(x => x.Game).WithMany(x => x.LibraryEntries)
            .HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
        entry.ToTable(t =>
        {
            t.HasCheckConstraint("CK_LibraryEntry_Rating", "Rating IS NULL OR Rating BETWEEN 0 AND 10");
            t.HasCheckConstraint("CK_LibraryEntry_Status", "Status IN (0, 1, 2, 3)");
            t.HasCheckConstraint("CK_LibraryEntry_AbandonmentReason", "Status = 3 OR AbandonmentReason IS NULL");
        });

        var ownership = model.Entity<GameOwnership>();
        ownership.HasIndex(x => new { x.LibraryEntryId, x.StoreId, x.PlatformId }).IsUnique();
        ownership.HasOne(x => x.LibraryEntry).WithMany(x => x.Ownerships)
            .HasForeignKey(x => x.LibraryEntryId).OnDelete(DeleteBehavior.Cascade);
        ownership.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        ownership.HasOne(x => x.Platform).WithMany().HasForeignKey(x => x.PlatformId).OnDelete(DeleteBehavior.Restrict);

        model.Entity<UserProfile>().HasData(new { Id = LocalProfile.Id, DisplayName = "Usuário local" });
        var stores = new[] { ("steam", "Steam"), ("gog", "GOG"), ("epic", "Epic Games Store"),
            ("playstation-store", "PlayStation Store"), ("microsoft-store", "Microsoft Store"),
            ("nintendo-eshop", "Nintendo eShop"), ("physical", "Mídia física") };
        for (var i = 0; i < stores.Length; i++)
            model.Entity<Store>().HasData(new { Id = i + 1, Code = stores[i].Item1, Name = stores[i].Item2 });
        var platforms = new[] { ("pc", "PC"), ("ps4", "PlayStation 4"), ("ps5", "PlayStation 5"),
            ("xbox-one", "Xbox One"), ("xbox-series", "Xbox Series X/S"), ("switch", "Nintendo Switch") };
        for (var i = 0; i < platforms.Length; i++)
            model.Entity<Platform>().HasData(new { Id = i + 1, Code = platforms[i].Item1, Name = platforms[i].Item2 });
    }

    private void ValidateEntities()
    {
        foreach (var item in ChangeTracker.Entries().Where(x =>
            x.State is EntityState.Added or EntityState.Modified))
            Validator.ValidateObject(item.Entity, new ValidationContext(item.Entity), validateAllProperties: true);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

// Only local identity for now; a future authenticated identity can resolve this profile.
public static class LocalProfile
{
    public const int Id = 1;
}
