using FluentValidation;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class CapacidadeProfessorDtoValidator : AbstractValidator<CapacidadeProfessorDto>
{
    public CapacidadeProfessorDtoValidator()
    {
        RuleFor(dto => dto.LimiteOrientandos)
            .InclusiveBetween(1, 20)
            .WithMessage("O limite de orientandos deve estar entre 1 e 20.");
    }
}
