using FluentValidation;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class UsuarioDtoValidator : AbstractValidator<UsuarioDto>
{
    public UsuarioDtoValidator()
    {
        RuleFor(dto => dto.Nome)
            .NotEmpty().WithMessage("O nome é obrigatório.")
            .MaximumLength(200).WithMessage("O nome deve ter no máximo 200 caracteres.");

        // Issue #99 (achado do QA do lote #70): MailboxAddress.TryParse aceita a forma RFC de
        // display-name ("Nome Exibido <email@x.com>") — sem essa checagem (dentro de
        // EmailValidoParaEnvio), esse valor passa na validação e no índice único de e-mail
        // (#65), mas o login (que compara e-mail literal) nunca bate com o que o usuário de
        // fato digitaria. Na prática, uma forma de o próprio usuário se trancar pra fora da
        // conta ao editar o e-mail com um cliente/copy-paste que inclua o nome de exibição.
        RuleFor(dto => dto.Email).EmailValidoParaEnvio();
    }
}
