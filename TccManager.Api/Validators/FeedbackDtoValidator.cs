using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class FeedbackDtoValidator : AbstractValidator<FeedbackDto>
{
    public FeedbackDtoValidator(ISanitizerService sanitizerService)
    {
        RuleFor(dto => dto.Nota)
            .InclusiveBetween(0, 10)
            .When(dto => dto.Nota.HasValue)
            .WithMessage("A nota deve estar entre 0 e 10.");

        RuleFor(dto => dto.Feedback)
            .NotEmpty().WithMessage("O feedback é obrigatório.")
            .MaximoCaracteresSanitizados(sanitizerService, 2000)
                .WithMessage("O feedback deve ter no máximo 2000 caracteres.");
    }
}
