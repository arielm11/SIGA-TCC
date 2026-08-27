using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class AcompanhamentoDtoValidator : AbstractValidator<AcompanhamentoDto>
{
    public AcompanhamentoDtoValidator(ISanitizerService sanitizerService)
    {
        // Issue #73 (achado A10-1 da revisão de segurança): OrientadorController persiste
        // _sanitizerService.Sanitizar(dto.Ata), não o valor cru — HtmlSanitizer só CODIFICA
        // entidades (nunca decodifica), então validar o comprimento cru permitia uma Ata
        // dentro do limite estourar a coluna nvarchar(4000) no INSERT. Medir o comprimento do
        // valor já sanitizado fecha esse descompasso.
        RuleFor(dto => dto.Ata)
            .NotEmpty().WithMessage("O registro da ata é obrigatório.")
            .Must(ata => (sanitizerService.Sanitizar(ata)?.Length ?? 0) <= 4000)
                .WithMessage("O registro da ata deve ter no máximo 4000 caracteres.");
    }
}
