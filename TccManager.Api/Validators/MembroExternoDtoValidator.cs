using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class MembroExternoDtoValidator : AbstractValidator<MembroExternoDto>
{
    public MembroExternoDtoValidator(ISanitizerService sanitizerService)
    {
        // Issue #73 (achado A10-1 da revisão de segurança): CoordenadorController persiste
        // _sanitizerService.Sanitizar(dto.Nome/Instituicao), não o valor cru — HtmlSanitizer
        // só CODIFICA entidades (nunca decodifica), então validar o comprimento cru permitia
        // um Nome/Instituicao dentro do limite estourar a coluna no INSERT. Medir o
        // comprimento do valor já sanitizado fecha esse descompasso. Email NÃO é sanitizado
        // (não passa por _sanitizerService.Sanitizar em nenhum ponto), então continua medido
        // pelo valor cru (ver EmailValidoParaEnvio).
        RuleFor(dto => dto.Nome)
            .NotEmpty().WithMessage("O nome é obrigatório.")
            .MaximoCaracteresSanitizados(sanitizerService, 200)
                .WithMessage("O nome deve ter no máximo 200 caracteres.");

        // Issue #99 (achado do QA do lote #70): mesma checagem de UsuarioDtoValidator — menos
        // crítico aqui (MembroExterno não faz login por senha, só recebe tokens de rascunho
        // por e-mail), mas mesmo assim vale manter por consistência com o outro validator.
        RuleFor(dto => dto.Email).EmailValidoParaEnvio();

        RuleFor(dto => dto.Instituicao)
            .NotEmpty().WithMessage("A instituição é obrigatória.")
            .MaximoCaracteresSanitizados(sanitizerService, 300)
                .WithMessage("A instituição deve ter no máximo 300 caracteres.");
    }
}
