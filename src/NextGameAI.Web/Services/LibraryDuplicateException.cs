using System.ComponentModel.DataAnnotations;

namespace NextGameAI.Web.Services;

public sealed class LibraryDuplicateException(int entryId)
    : ValidationException("Este jogo já está na sua biblioteca. Você pode consultar os detalhes ou editar a entrada existente.")
{
    public int EntryId { get; } = entryId;
}
