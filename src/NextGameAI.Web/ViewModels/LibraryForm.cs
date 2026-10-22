using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using NextGameAI.Web.Models;

namespace NextGameAI.Web.ViewModels;

public class LibraryForm : IValidatableObject
{
    public int? ExistingGameId { get; set; }
    [Display(Name = "Título"), StringLength(300, ErrorMessage = "O título deve ter até 300 caracteres.")]
    public string? Title { get; set; }
    [Display(Name = "Edição"), StringLength(200, ErrorMessage = "A edição deve ter até 200 caracteres.")]
    public string? EditionTitle { get; set; }
    [Display(Name = "Status"), EnumDataType(typeof(LibraryStatus), ErrorMessage = "Selecione um status válido.")]
    public LibraryStatus Status { get; set; }
    [Display(Name = "Avaliação"), Range(0, 10, ErrorMessage = "A avaliação deve ser de 0 a 5 estrelas, em passos de meia estrela.")]
    public int? Rating { get; set; }
    [Display(Name = "Observações"), StringLength(5000, ErrorMessage = "As observações devem ter até 5.000 caracteres.")]
    public string? Notes { get; set; }
    [Display(Name = "Motivo de abandono"), StringLength(1000, ErrorMessage = "O motivo deve ter até 1.000 caracteres.")]
    public string? AbandonmentReason { get; set; }
    public List<OwnershipInput> Ownerships { get; set; } = [];
    public List<SelectListItem> Games { get; set; } = [];
    public List<SelectListItem> Stores { get; set; } = [];
    public List<SelectListItem> Platforms { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ExistingGameId is null && string.IsNullOrWhiteSpace(Title))
            yield return new ValidationResult("Informe o título do novo jogo ou selecione um jogo do catálogo.", [nameof(Title)]);
        if (ExistingGameId is <= 0)
            yield return new ValidationResult("Selecione um jogo válido.", [nameof(ExistingGameId)]);
        if (Ownerships.Count == 0)
            yield return new ValidationResult("Informe ao menos uma combinação de loja e plataforma.", [nameof(Ownerships)]);
        if (Ownerships.Any(x => x.StoreId <= 0 || x.PlatformId <= 0))
            yield return new ValidationResult("Selecione a loja e a plataforma de cada posse.", [nameof(Ownerships)]);
        if (Ownerships.Select(x => (x.StoreId, x.PlatformId)).Distinct().Count() != Ownerships.Count)
            yield return new ValidationResult("Não repita a mesma combinação de loja e plataforma.", [nameof(Ownerships)]);
    }
}

public class OwnershipInput
{
    public int StoreId { get; set; }
    public int PlatformId { get; set; }
}
