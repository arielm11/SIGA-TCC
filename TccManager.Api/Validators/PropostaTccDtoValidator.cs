using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class PropostaTccDtoValidator : AbstractValidator<PropostaTccDto>
{
    public PropostaTccDtoValidator(ISanitizerService sanitizerService)
    {
        // Issue #73 (achado A10-1 da revisão de segurança, docs/seguranca/2026-08-27-fix-campos-texto-livre-maxlength.md):
        // Titulo perdeu o [StringLength(200)] de DataAnnotations no DTO (que validava o valor
        // cru, não o sanitizado) em favor da regra abaixo.
        RuleFor(dto => dto.Titulo)
            .NotEmpty().WithMessage("O título é obrigatório.")
            .MaximoCaracteresSanitizados(sanitizerService, 200)
                .WithMessage("O título deve ter no máximo 200 caracteres.");

        RuleFor(dto => dto.Resumo)
            .NotEmpty().WithMessage("O resumo é obrigatório.")
            .MaximoCaracteresSanitizados(sanitizerService, 4000)
                .WithMessage("O resumo deve ter no máximo 4000 caracteres.");

        // Issue #112 (P1): campo opcional — só valida formato (> 0) quando informado.
        // Existência/papel/ativo do professor são checados no controller (validadores não
        // consultam banco, mesmo racional de DesignarOrientadorDto).
        RuleFor(dto => dto.OrientadorSolicitadoId)
            .GreaterThan(0).WithMessage("Professor solicitado inválido.")
            .When(dto => dto.OrientadorSolicitadoId.HasValue);
    }
}
