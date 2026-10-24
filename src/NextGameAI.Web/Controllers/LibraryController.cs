using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.ViewModels;
using NextGameAI.Web.Services.Igdb;
using Microsoft.Data.Sqlite;

namespace NextGameAI.Web.Controllers;

public class LibraryController(LibraryService library) : Controller
{
    private const string DatabaseAccessMessage = "Não foi possível acessar a biblioteca agora. Seus dados foram mantidos. Tente novamente.";
    // Expected access failures only: busy, locked, read-only, I/O, full disk and cannot open.
    // SQL/schema errors and unrelated programming exceptions must still reach normal diagnostics.
    private static bool IsExpectedAccessFailure(SqliteException error) =>
        error.SqliteErrorCode is 5 or 6 or 8 or 10 or 13 or 14;
    public async Task<IActionResult> Index() => View(await (await library.QueryAsync())
        .AsNoTracking().OrderBy(x => x.Game.Title).ToListAsync());

    public async Task<IActionResult> Details(int id)
    {
        var entry = await (await library.QueryAsync()).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return entry is null ? NotFound() : View(entry);
    }

    public async Task<IActionResult> Create(long? igdbId, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest();
        var form = new SimpleLibraryForm { IgdbId = igdbId, IsIgdbSelection = igdbId.HasValue };
        if (igdbId.HasValue) await PrepareIgdbAsync(form, cancellationToken);
        return View(form);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SimpleLibraryForm form, CancellationToken cancellationToken)
    {
        var igdbSelection = form.IgdbId.HasValue || Request.Form.ContainsKey(nameof(form.IgdbId));
        form.IsIgdbSelection = igdbSelection;
        if (igdbSelection)
        {
            // Required/manual title validation cannot make posted metadata authoritative.
            ModelState.Remove(nameof(form.Title));
            if (form.IgdbId.HasValue) await PrepareIgdbAsync(form, cancellationToken);
            else ModelState.AddModelError(nameof(form.IgdbId), "Selecione um identificador IGDB válido.");
        }
        if (ModelState.IsValid)
        {
            try { return RedirectToAction(nameof(Details), new { id = igdbSelection
                ? await library.SaveIgdbAsync(form, cancellationToken) : await library.SaveSimpleAsync(form) }); }
            catch (LibraryDuplicateException ex) { form.DuplicateEntryId = ex.EntryId; ModelState.AddModelError("", ex.Message); }
            catch (IgdbQueryException) { ModelState.AddModelError("", "Não foi possível consultar o jogo na IGDB agora. Seus dados foram mantidos. Tente novamente."); }
            catch (ValidationException ex) { ModelState.AddModelError("", ex.Message); }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlite && IsExpectedAccessFailure(sqlite))
            { ModelState.AddModelError("", DatabaseAccessMessage); }
            catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            { ModelState.AddModelError("", "Não foi possível salvar. Verifique se o jogo ou a posse já foi cadastrado e tente novamente."); }
            catch (SqliteException ex) when (IsExpectedAccessFailure(ex)) { ModelState.AddModelError("", DatabaseAccessMessage); }
        }
        return View(form);
    }

    private async Task PrepareIgdbAsync(SimpleLibraryForm form, CancellationToken ct)
    {
        try
        {
            var game = await library.ResolveIgdbAsync(form.IgdbId!.Value, ct);
            form.SelectedGame = CatalogGameResult.FromGame(game);
            form.Title = game.Title;
            ModelState.Remove(nameof(form.Title));
        }
        catch (IgdbQueryException) { ModelState.AddModelError("", "Não foi possível consultar o jogo na IGDB agora. Seus dados foram mantidos. Tente novamente."); }
        catch (ValidationException ex) { ModelState.AddModelError("", ex.Message); }
        catch (SqliteException ex) when (IsExpectedAccessFailure(ex)) { ModelState.AddModelError("", DatabaseAccessMessage); }
    }

    public async Task<IActionResult> Edit(int id)
    {
        var entry = await (await library.QueryAsync()).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (entry is null) return NotFound();
        var form = new SimpleLibraryForm
        {
            Title = entry.Game.Title,
            Status = entry.Status, Rating = entry.Rating, Notes = entry.Notes, AbandonmentReason = entry.AbandonmentReason,
            SelectedPlatforms = entry.Ownerships.Select(x => PlatformGroups.FromPlatformCode(x.Platform.Code)).Distinct().ToList()
        };
        PrepareEdit(form, entry);
        return View(form);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, SimpleLibraryForm form)
    {
        var entry = await (await library.QueryAsync()).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (entry is null) return NotFound();
        if (ModelState.IsValid)
        {
            try
            {
                await library.SaveSimpleAsync(form, id);
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (ValidationException ex) { ModelState.AddModelError("", ex.Message); }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (DbUpdateException) { ModelState.AddModelError("", "Não foi possível salvar. Confira as posses e tente novamente."); }
        }
        PrepareEdit(form, entry);
        return View(form);
    }

    public async Task<IActionResult> Delete(int id)
    {
        var entry = await (await library.QueryAsync()).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return entry is null ? NotFound() : View(entry);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        if (!await library.DeleteAsync(id)) return NotFound();
        return RedirectToAction(nameof(Index));
    }

    private static void PrepareEdit(SimpleLibraryForm form, LibraryEntry entry)
    {
        form.IsEditing = true;
        form.Title = entry.Game.Title;
        form.ExistingPlatformCounts = entry.Ownerships.GroupBy(x => PlatformGroups.FromPlatformCode(x.Platform.Code))
            .ToDictionary(x => x.Key, x => x.Count());
    }
}
