using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class RejeicaoDtoValidator : AbstractValidator<RejeicaoDto>
{
    public RejeicaoDtoValidator(ISanitizerService sanitizerService)
    {
        // Issue #73 (achado A10-1 da revisão de segurança): CoordenadorController persiste
        // _sanitizerService.Sanitizar(dto.Motivo), não o valor cru — HtmlSanitizer só CODIFICA
        // entidades (nunca decodifica), então validar o comprimento cru permitia um Motivo
        // dentro do limite estourar a coluna nvarchar(2000) no INSERT. Medir o comprimento do
        // valor já sanitizado fecha esse descompasso.
        RuleFor(dto => dto.Motivo)
            .NotEmpty().WithMessage("O motivo da rejeição é obrigatório!")
            .Must(motivo => (sanitizerService.Sanitizar(motivo)?.Length ?? 0) <= 2000)
                .WithMessage("O motivo da rejeição deve ter no máximo 2000 caracteres.");
    }
}
