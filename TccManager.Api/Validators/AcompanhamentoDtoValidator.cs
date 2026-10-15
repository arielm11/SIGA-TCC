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

        RuleFor(dto => dto.Ata)
            .NotEmpty().WithMessage("O registro da ata é obrigatório.")
            .MaximoCaracteresSanitizados(sanitizerService, 4000)
                .WithMessage("O registro da ata deve ter no máximo 4000 caracteres.");
    }
}
