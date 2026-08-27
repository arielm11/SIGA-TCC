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

        // Issue #73 (achado A10-1 da revisão de segurança): OrientadorController persiste
        // _sanitizerService.Sanitizar(dto.Feedback), não o valor cru — HtmlSanitizer só
        // CODIFICA entidades (nunca decodifica), então validar o comprimento cru permitia um
        // Feedback dentro do limite estourar a coluna nvarchar(2000) no INSERT. Medir o
        // comprimento do valor já sanitizado fecha esse descompasso.
        RuleFor(dto => dto.Feedback)
            .NotEmpty().WithMessage("O feedback é obrigatório.")
            .Must(feedback => (sanitizerService.Sanitizar(feedback)?.Length ?? 0) <= 2000)
                .WithMessage("O feedback deve ter no máximo 2000 caracteres.");
    }
}
