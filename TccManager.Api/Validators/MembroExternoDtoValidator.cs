using FluentValidation;
using MimeKit;
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
        // pelo valor cru.
        RuleFor(dto => dto.Nome)
            .NotEmpty().WithMessage("O nome é obrigatório.")
            .Must(nome => (sanitizerService.Sanitizar(nome)?.Length ?? 0) <= 200)
                .WithMessage("O nome deve ter no máximo 200 caracteres.");

        RuleFor(dto => dto.Email)
            .NotEmpty().WithMessage("O email é obrigatório.")
            .EmailAddress().WithMessage("O email informado não é válido.")
            .MaximumLength(450).WithMessage("O email deve ter no máximo 450 caracteres.")
            // EmailAddress() do FluentValidation é mais permissivo que o parser usado no
            // envio (MailboxAddress.Parse em MailKitEmailService) — sem esta checagem, um
            // e-mail passa na validação e só falha silenciosamente na hora de enviar
            // (ParseException, descartado com um warning). Mesmo parser nas duas pontas.
            .Must(email => MailboxAddress.TryParse(email, out _))
                .WithMessage("O email informado não é aceito pelo servidor de e-mail.")
            // Issue #99 (achado do QA do lote #70): mesma checagem de UsuarioDtoValidator —
            // MailboxAddress.TryParse aceita a forma de display-name. Menos crítico aqui
            // (MembroExterno não faz login por senha, só recebe tokens de rascunho por
            // e-mail), mas mesmo assim vale rejeitar por consistência com o outro validator.
            .Must(email => !MailboxAddress.TryParse(email, out var endereco) || endereco!.Address == email)
                .WithMessage("O email deve conter apenas o endereço, sem nome de exibição (ex.: \"nome@dominio.com\", não \"Nome <nome@dominio.com>\").");

        RuleFor(dto => dto.Instituicao)
            .NotEmpty().WithMessage("A instituição é obrigatória.")
            .Must(instituicao => (sanitizerService.Sanitizar(instituicao)?.Length ?? 0) <= 300)
                .WithMessage("A instituição deve ter no máximo 300 caracteres.");
    }
}
