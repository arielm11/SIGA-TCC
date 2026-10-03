using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class AcompanhamentoDtoValidator : AbstractValidator<AcompanhamentoDto>
{
    public AcompanhamentoDtoValidator(ISanitizerService sanitizerService)
    {
        // Issue #146 (achado D9): não havia nenhuma validação de presença para DataReuniao —
        // combinado com o inicializador não-vazio que existia em AcompanhamentoDto (removido
        // nesta mesma correção), um cliente que esquecesse de enviar o campo registrava a
        // reunião silenciosamente como "hoje". NotEmpty() sobre DateTime rejeita
        // especificamente default(DateTime) (ano 0001).
        RuleFor(dto => dto.DataReuniao)
            .NotEmpty().WithMessage("A data da reunião é obrigatória.");

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
