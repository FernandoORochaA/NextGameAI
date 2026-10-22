using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.ViewModels;

namespace NextGameAI.Web.Controllers;

public class LibraryController(LibraryService library) : Controller
{
    public async Task<IActionResult> Index() => View(await (await library.QueryAsync())
        .AsNoTracking().OrderBy(x => x.Game.Title).ToListAsync());

    public async Task<IActionResult> Details(int id)
    {
        var entry = await (await library.QueryAsync()).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return entry is null ? NotFound() : View(entry);
    }

    public IActionResult Create() => View(new SimpleLibraryForm());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SimpleLibraryForm form)
    {
        if (ModelState.IsValid)
        {
            try { return RedirectToAction(nameof(Details), new { id = await library.SaveSimpleAsync(form) }); }
            catch (ValidationException ex) { ModelState.AddModelError("", ex.Message); }
            catch (DbUpdateException) { ModelState.AddModelError("", "Não foi possível salvar. Verifique se o jogo ou a posse já foi cadastrado e tente novamente."); }
        }
        return View(form);
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
