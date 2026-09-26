using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class AgendarBancaDtoValidator : AbstractValidator<AgendarBancaDto>
{
    public AgendarBancaDtoValidator(TimeProvider timeProvider)
    {
        RuleFor(dto => dto.DataHora)
            .Must(dataHora => BrasiliaTimeZoneService.ConverterDeBrasiliaParaUtc(dataHora) > timeProvider.GetUtcNow().UtcDateTime)
            .WithMessage("A data e hora da banca devem ser futuras.");

        // Issue #73: Local não tinha limite nenhum — mesmo valor de Banca.Local
        // ([MaxLength(300)]).
        RuleFor(dto => dto.Local)
            .NotEmpty().WithMessage("O local ou link é obrigatório.")
            .MaximumLength(300).WithMessage("O local ou link deve ter no máximo 300 caracteres.");

        // Issue #105: as duas listas não tinham teto de contagem — não é texto livre (fora
        // do escopo literal da #73), mas é a mesma classe de lacuna (entrada sem teto). 10
        // é um valor de julgamento próprio, bem acima de qualquer banca real (RN05 exige só
        // um mínimo de 2 avaliadores além do orientador), só para limitar o custo de uma
        // requisição com uma lista de ids absurdamente grande.
        const int maximoDeAvaliadoresPorLista = 10;

        RuleFor(dto => dto.ProfessoresIds)
            .Must(lista => lista.Count <= maximoDeAvaliadoresPorLista)
                .WithMessage($"É possível informar no máximo {maximoDeAvaliadoresPorLista} professores avaliadores.");

        RuleFor(dto => dto.MembrosExternosIds)
            .Must(lista => lista.Count <= maximoDeAvaliadoresPorLista)
                .WithMessage($"É possível informar no máximo {maximoDeAvaliadoresPorLista} membros externos avaliadores.");
    }
}
