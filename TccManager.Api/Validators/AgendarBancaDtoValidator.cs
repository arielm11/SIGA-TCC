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
    }
}
