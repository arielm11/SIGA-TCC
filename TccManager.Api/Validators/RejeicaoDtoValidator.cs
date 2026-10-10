using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class RejeicaoDtoValidator : AbstractValidator<RejeicaoDto>
{
    public RejeicaoDtoValidator(ISanitizerService sanitizerService)
    {
        // Resolvido pelo tipo do DTO via FluentValidationActionFilter — vale para qualquer
        // controller que use RejeicaoDto.
        RuleFor(dto => dto.Motivo)
            .NotEmpty().WithMessage("O motivo da rejeição é obrigatório!")
            .MaximoCaracteresSanitizados(sanitizerService, 2000)
                .WithMessage("O motivo da rejeição deve ter no máximo 2000 caracteres.");
    }
}
