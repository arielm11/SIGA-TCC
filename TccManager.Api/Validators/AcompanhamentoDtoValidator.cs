using FluentValidation;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class AcompanhamentoDtoValidator : AbstractValidator<AcompanhamentoDto>
{
    public AcompanhamentoDtoValidator()
    {
    }
}
