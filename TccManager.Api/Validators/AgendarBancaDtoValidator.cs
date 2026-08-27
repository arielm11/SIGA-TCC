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
    }
}
