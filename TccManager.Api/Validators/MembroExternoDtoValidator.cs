using FluentValidation;
using MimeKit;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class MembroExternoDtoValidator : AbstractValidator<MembroExternoDto>
{
    public MembroExternoDtoValidator()
    {
        RuleFor(dto => dto.Nome)
            .NotEmpty().WithMessage("O nome é obrigatório.");

        RuleFor(dto => dto.Email)
            .NotEmpty().WithMessage("O email é obrigatório.")
            .EmailAddress().WithMessage("O email informado não é válido.")
            .MaximumLength(450).WithMessage("O email deve ter no máximo 450 caracteres.")
            // EmailAddress() do FluentValidation é mais permissivo que o parser usado no
            // envio (MailboxAddress.Parse em MailKitEmailService) — sem esta checagem, um
            // e-mail passa na validação e só falha silenciosamente na hora de enviar
            // (ParseException, descartado com um warning). Mesmo parser nas duas pontas.
            .Must(email => MailboxAddress.TryParse(email, out _))
                .WithMessage("O email informado não é aceito pelo servidor de e-mail.");

        RuleFor(dto => dto.Instituicao)
            .NotEmpty().WithMessage("A instituição é obrigatória.");
    }
}
