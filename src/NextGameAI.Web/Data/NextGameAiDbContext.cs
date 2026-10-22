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

    protected override void OnModelCreating(ModelBuilder model)
    {
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
