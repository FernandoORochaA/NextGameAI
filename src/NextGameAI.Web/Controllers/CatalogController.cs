using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NextGameAI.Web.Services.Igdb;
using NextGameAI.Web.ViewModels;

namespace NextGameAI.Web.Controllers;

public sealed class CatalogController(IIgdbClient igdb) : Controller
{
    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet]
    [EnableRateLimiting("catalog-search")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Search(string? term, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !ValidTerm(term))
            return Failure(400, "invalidInput", "Digite entre 3 e 100 caracteres para pesquisar.");
        try
        {
            var games = await igdb.SearchAsync(term!, cancellationToken);
            return Json(games.Take(10).Select(CatalogGameResult.FromGame).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (IgdbQueryException error)
        {
            return error.Failure switch
            {
                IgdbQueryFailure.InvalidInput => Failure(400, "invalidInput", "Digite entre 3 e 100 caracteres para pesquisar."),
                IgdbQueryFailure.AuthenticationUnavailable or IgdbQueryFailure.Unauthorized =>
                    Failure(503, "authentication", "Não foi possível autorizar a pesquisa. Tente novamente mais tarde."),
                IgdbQueryFailure.RateLimited =>
                    Failure(429, "rateLimited", "Muitas pesquisas em pouco tempo. Aguarde um momento e tente novamente."),
                _ => Failure(503, "unavailable", "A pesquisa de jogos está temporariamente indisponível. Tente novamente mais tarde.")
            };
        }
        catch (Exception)
        {
            // Never send external exception messages, headers or bodies to the browser or logs.
            return Failure(500, "unexpected", "Não foi possível concluir a pesquisa. Tente novamente.");
        }
    }

    private ObjectResult Failure(int status, string code, string message) =>
        StatusCode(status, new { code, message });

    private static bool ValidTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length > 1024 || term.Any(c => char.IsControl(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            return false;
        try { return Regex.Replace(term.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ").Length is >= 3 and <= 100; }
        catch (ArgumentException) { return false; }
    }
}
