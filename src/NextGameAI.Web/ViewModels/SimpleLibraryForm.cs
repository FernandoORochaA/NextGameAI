using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NextGameAI.Web.Models;

namespace NextGameAI.Web.ViewModels;

public class SimpleLibraryForm : IValidatableObject
{
    [Required(ErrorMessage = "Informe o título do jogo.")]
    [StringLength(300, ErrorMessage = "O título deve ter até 300 caracteres.")]
    [Display(Name = "Título do jogo")]
    public string Title { get; set; } = "";

    [EnumDataType(typeof(LibraryStatus), ErrorMessage = "Selecione um status válido.")]
    public LibraryStatus Status { get; set; }

    [Range(0, 10, ErrorMessage = "A avaliação deve ser de 0 a 5 estrelas, em passos de meia estrela.")]
    public int? Rating { get; set; }

    [Display(Name = "Observações"), StringLength(5000, ErrorMessage = "As observações devem ter até 5.000 caracteres.")]
    public string? Notes { get; set; }

    [Display(Name = "Motivo de abandono"), StringLength(1000, ErrorMessage = "O motivo deve ter até 1.000 caracteres.")]
    public string? AbandonmentReason { get; set; }

    public List<string> SelectedPlatforms { get; set; } = [];
    public List<string> ConfirmedPlatformRemovals { get; set; } = [];

    // Display-only state, rebuilt from persisted ownerships on GET and invalid POST.
    [BindNever] public Dictionary<string, int> ExistingPlatformCounts { get; set; } = [];
    [BindNever] public bool IsEditing { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SelectedPlatforms.Count == 0)
            yield return new ValidationResult("Selecione ao menos uma plataforma.", [nameof(SelectedPlatforms)]);
        if (SelectedPlatforms.Any(x => !PlatformGroups.Choices.Any(c => c.Code == x)))
            yield return new ValidationResult("Selecione uma plataforma válida.", [nameof(SelectedPlatforms)]);
        if (SelectedPlatforms.Distinct(StringComparer.Ordinal).Count() != SelectedPlatforms.Count)
            yield return new ValidationResult("Não repita uma plataforma.", [nameof(SelectedPlatforms)]);
        if (ConfirmedPlatformRemovals.Any(x => !PlatformGroups.Choices.Any(c => c.Code == x)) ||
            ConfirmedPlatformRemovals.Distinct(StringComparer.Ordinal).Count() != ConfirmedPlatformRemovals.Count)
            yield return new ValidationResult("A confirmação de remoção contém plataformas inválidas ou repetidas.", [nameof(ConfirmedPlatformRemovals)]);
    }
}

public static class PlatformGroups
{
    public static IEnumerable<string> DisplayNames(IEnumerable<GameOwnership> ownerships)
    {
        var groups = ownerships.Select(x => FromPlatformCode(x.Platform.Code)).ToHashSet();
        return Choices.Where(x => groups.Contains(x.Code)).Select(x => x.Name);
    }

    public static IReadOnlyList<(string Code, string Name)> Choices { get; } =
        Array.AsReadOnly(new[] { ("pc", "PC"), ("playstation", "PlayStation"), ("nintendo", "Nintendo"),
            ("xbox", "Xbox"), ("other", "Outros") });

    // Explicit codes only. Unknown/legacy custom platforms retain their identity under Outros.
    public static string FromPlatformCode(string code) => code switch
    {
        "pc" => "pc",
        "playstation" or "ps1" or "ps2" or "ps3" or "ps4" or "ps5" or "psp" or "ps-vita" => "playstation",
        "nintendo" or "switch" or "switch-2" or "wii" or "wii-u" or "3ds" or "ds" => "nintendo",
        "xbox" or "xbox-one" or "xbox-series" or "xbox-360" => "xbox",
        _ => "other"
    };
}
