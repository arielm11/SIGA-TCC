using FluentValidation;
using TccManager.Api.Services;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class PropostaTccDtoValidator : AbstractValidator<PropostaTccDto>
{
    public PropostaTccDtoValidator(ISanitizerService sanitizerService)
    {
        // Issue #73 (achado A10-1 da revisão de segurança, docs/seguranca/2026-08-27-fix-campos-texto-livre-maxlength.md):
        // TccController.CriarProposta persiste _sanitizerService.Sanitizar(dto.Titulo/Resumo),
        // não o valor cru — e HtmlSanitizer CODIFICA entidades (ex.: "&" -> "&amp;", 5x maior),
        // nunca decodifica. Validar o comprimento do valor CRU permitia um Titulo/Resumo dentro
        // do limite passar na validação e estourar a coluna nvarchar(N) na hora do INSERT (o
        // mesmo tipo de falha "silenciosa do ponto de vista do usuário" que a issue pede para
        // eliminar — vira 500 genérico via GlobalExceptionHandler, não um 400 claro). Medir o
        // comprimento do valor JÁ sanitizado fecha esse descompasso; Titulo perdeu o
        // [StringLength(200)] de DataAnnotations no DTO (que sofria do mesmo problema) em favor
        // desta regra.
        RuleFor(dto => dto.Titulo)
            .NotEmpty().WithMessage("O título é obrigatório.")
            .Must(titulo => (sanitizerService.Sanitizar(titulo)?.Length ?? 0) <= 200)
                .WithMessage("O título deve ter no máximo 200 caracteres.");

        RuleFor(dto => dto.Resumo)
            .NotEmpty().WithMessage("O resumo é obrigatório.")
            .Must(resumo => (sanitizerService.Sanitizar(resumo)?.Length ?? 0) <= 4000)
                .WithMessage("O resumo deve ter no máximo 4000 caracteres.");
    }
}
