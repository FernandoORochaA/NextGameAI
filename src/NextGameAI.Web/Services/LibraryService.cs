using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.ViewModels;
using NextGameAI.Web.Services.Igdb;
using Microsoft.Data.Sqlite;

namespace NextGameAI.Web.Services;

public class LibraryService(NextGameAiDbContext db, CurrentUserProfile currentProfile, IIgdbClient? igdb = null)
{
    public async Task<Game> ResolveIgdbAsync(long id, CancellationToken ct = default)
    {
        if (id <= 0) throw new ValidationException("Selecione um identificador IGDB válido.");
        var local = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.IgdbId == id, ct);
        if (local is not null) return local;
        var result = await (igdb ?? throw new InvalidOperationException()).GetByIdAsync(id, ct);
        if (result is null || result.IgdbId != id)
            throw new ValidationException("O jogo selecionado não foi encontrado na IGDB. Volte à pesquisa e selecione outro título.");
        var game = new Game
        {
            IgdbId = result.IgdbId, Title = result.Title, EditionTitle = result.EditionTitle,
            CoverImageId = result.CoverImageId, FirstReleaseDate = result.FirstReleaseDate,
            GameTypeId = result.GameTypeId, GameType = result.GameType
        };
        Validator.ValidateObject(game, new ValidationContext(game), true);
        return game;
    }

    public async Task<int> SaveIgdbAsync(SimpleLibraryForm input, CancellationToken ct = default)
    {
        if (input.IgdbId is null) throw new ValidationException("Selecione um identificador IGDB válido.");
        // Resolve identity before starting a SQLite transaction; browser metadata is never authoritative.
        var game = await ResolveIgdbAsync(input.IgdbId.Value, ct);
        var trusted = new SimpleLibraryForm
        {
            Title = game.Title, IgdbId = game.IgdbId, Status = input.Status, Rating = input.Rating,
            Notes = input.Notes, AbandonmentReason = input.AbandonmentReason,
            SelectedPlatforms = input.SelectedPlatforms, ConfirmedPlatformRemovals = input.ConfirmedPlatformRemovals
        };
        try { return await SaveSimpleCoreAsync(trusted, null, game); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            // Another request may have inserted this identity. Recheck after rollback, retry only once.
            var local = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.IgdbId == game.IgdbId, ct);
            if (local is null) throw;
            return await SaveSimpleCoreAsync(trusted, null, local);
        }
    }

    public async Task<IQueryable<LibraryEntry>> QueryAsync()
    {
        var profileId = await currentProfile.GetIdAsync();
        return db.LibraryEntries.Where(x => x.UserProfileId == profileId)
            .Include(x => x.Game).Include(x => x.Ownerships).ThenInclude(x => x.Store)
            .Include(x => x.Ownerships).ThenInclude(x => x.Platform);
    }

    public Task<int> SaveAsync(LibraryForm input, int? entryId = null) =>
        InTransactionAsync(() => SaveCoreAsync(input, entryId));

    public Task<int> SaveSimpleAsync(SimpleLibraryForm input, int? entryId = null)
    {
        if (input.IgdbId.HasValue && !entryId.HasValue)
            throw new ValidationException("Utilize o cadastro IGDB para o jogo selecionado.");
        return SaveSimpleCoreAsync(input, entryId, null);
    }

    private Task<int> SaveSimpleCoreAsync(SimpleLibraryForm input, int? entryId, Game? selectedGame)
    {
        Validator.ValidateObject(input, new ValidationContext(input), true);
        return InTransactionAsync(async () =>
        {
            Game? catalog = null;
            if (selectedGame is not null)
            {
                catalog = await db.Games.SingleOrDefaultAsync(g => g.IgdbId == selectedGame.IgdbId);
                if (catalog is not null)
                {
                    var profileId = await currentProfile.GetIdAsync();
                    var duplicate = await db.LibraryEntries.SingleOrDefaultAsync(e => e.GameId == catalog.Id && e.UserProfileId == profileId);
                    if (duplicate is not null) throw new LibraryDuplicateException(duplicate.Id);
                }
                else
                {
                    catalog = selectedGame;
                    db.Games.Add(catalog);
                    await db.SaveChangesAsync();
                }
            }
            LibraryEntry? existing = null;
            if (entryId.HasValue)
                existing = await (await QueryAsync()).SingleOrDefaultAsync(x => x.Id == entryId)
                    ?? throw new KeyNotFoundException("Entrada não encontrada.");

            var represented = existing?.Ownerships.Select(x => PlatformGroups.FromPlatformCode(x.Platform.Code))
                .ToHashSet() ?? [];
            var selected = input.SelectedPlatforms.ToHashSet();
            var removed = represented.Except(selected).ToHashSet();
            if (!removed.SetEquals(input.ConfirmedPlatformRemovals))
                throw new ValidationException("Confirme as plataformas que deseja remover antes de salvar.");

            // Keep every detailed row of retained groups; only confirmed groups leave the desired set.
            var ownerships = existing?.Ownerships
                .Where(x => selected.Contains(PlatformGroups.FromPlatformCode(x.Platform.Code)))
                .Select(x => new OwnershipInput { StoreId = x.StoreId, PlatformId = x.PlatformId }).ToList() ?? [];
            foreach (var code in input.SelectedPlatforms.Where(x => !represented.Contains(x)))
            {
                var store = await db.Stores.SingleOrDefaultAsync(x => x.Code == "unspecified");
                if (store is null)
                {
                    store = new Store { Code = "unspecified", Name = "Loja não informada" };
                    db.Stores.Add(store);
                }
                var platform = await db.Platforms.SingleOrDefaultAsync(x => x.Code == code);
                if (platform is null)
                {
                    platform = new Platform { Code = code, Name = PlatformGroups.Choices.Single(x => x.Code == code).Name };
                    db.Platforms.Add(platform);
                }
                // Resolve reference IDs inside the same transaction. A later failure rolls these back too.
                await db.SaveChangesAsync();
                ownerships.Add(new OwnershipInput { StoreId = store.Id, PlatformId = platform.Id });
            }

            return await SaveCoreAsync(new LibraryForm
            {
                ExistingGameId = existing?.GameId ?? catalog?.Id,
                Title = existing?.Game.Title ?? input.Title,
                EditionTitle = existing?.Game.EditionTitle,
                Status = input.Status, Rating = input.Rating, Notes = input.Notes,
                AbandonmentReason = input.AbandonmentReason, Ownerships = ownerships
            }, entryId);
        });
    }

    private async Task<int> InTransactionAsync(Func<Task<int>> action)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var id = await action();
            await transaction.CommitAsync();
            return id;
        }
        catch
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<int> SaveCoreAsync(LibraryForm input, int? entryId)
    {
        // The service also validates callers outside MVC. Never mutate tracked entities on invalid input.
        Validator.ValidateObject(input, new ValidationContext(input), true);
        var storeIds = input.Ownerships.Select(x => x.StoreId).Distinct().ToArray();
        var platformIds = input.Ownerships.Select(x => x.PlatformId).Distinct().ToArray();
        if (await db.Stores.CountAsync(x => storeIds.Contains(x.Id)) != storeIds.Length ||
            await db.Platforms.CountAsync(x => platformIds.Contains(x.Id)) != platformIds.Length)
            throw new ValidationException("Uma loja ou plataforma selecionada não existe.");

        var profileId = await currentProfile.GetIdAsync();
        LibraryEntry entry;
        if (entryId.HasValue)
        {
            entry = await db.LibraryEntries.Include(x => x.Ownerships)
                .SingleOrDefaultAsync(x => x.Id == entryId && x.UserProfileId == profileId)
                ?? throw new KeyNotFoundException("Entrada não encontrada.");
            // Editing personal data never changes or replaces a shared catalog record.
            if (input.ExistingGameId != entry.GameId)
                throw new ValidationException("O jogo de catálogo não pode ser trocado nesta edição.");
        }
        else
        {
            Game game;
            if (input.ExistingGameId.HasValue)
            {
                game = await db.Games.FindAsync(input.ExistingGameId.Value)
                    ?? throw new ValidationException("O jogo selecionado não existe no catálogo.");
                if (await db.LibraryEntries.AnyAsync(x => x.UserProfileId == profileId && x.GameId == game.Id))
                    throw new ValidationException("Este jogo já está na sua biblioteca. Edite a entrada existente.");
            }
            else
            {
                game = new Game { Title = input.Title!.Trim(), EditionTitle = Clean(input.EditionTitle) };
            }
            entry = new LibraryEntry { UserProfileId = profileId, Game = game, AddedAtUtc = DateTime.UtcNow };
            db.LibraryEntries.Add(entry);
        }

        entry.Status = input.Status;
        entry.Rating = input.Rating;
        if (entry.Notes != input.Notes) entry.Notes = Clean(input.Notes);
        entry.AbandonmentReason = input.Status == LibraryStatus.Abandoned ? Clean(input.AbandonmentReason) : null;
        entry.UpdatedAtUtc = DateTime.UtcNow;
        var desired = input.Ownerships.Select(x => (x.StoreId, x.PlatformId)).ToHashSet();
        foreach (var old in entry.Ownerships.Where(x => !desired.Contains((x.StoreId, x.PlatformId))).ToList())
        {
            db.GameOwnerships.Remove(old);
            entry.Ownerships.Remove(old);
        }
        foreach (var item in desired.Where(x => !entry.Ownerships.Any(o => o.StoreId == x.StoreId && o.PlatformId == x.PlatformId)))
            entry.Ownerships.Add(new GameOwnership { StoreId = item.StoreId, PlatformId = item.PlatformId });

        await db.SaveChangesAsync();
        return entry.Id;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var profileId = await currentProfile.GetIdAsync();
        var entry = await db.LibraryEntries.SingleOrDefaultAsync(x => x.Id == id && x.UserProfileId == profileId);
        if (entry is null) return false;
        db.LibraryEntries.Remove(entry);
        await db.SaveChangesAsync(); // Atomic; only ownerships cascade, the shared Game remains.
        return true;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
