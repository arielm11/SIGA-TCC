using FluentValidation;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class UsuarioDtoValidator : AbstractValidator<UsuarioDto>
{
    public UsuarioDtoValidator()
    {
        RuleFor(dto => dto.Nome)
            .NotEmpty().WithMessage("O nome é obrigatório.");

        RuleFor(dto => dto.Email)
            .NotEmpty().WithMessage("O email é obrigatório.")
            .EmailAddress().WithMessage("O email informado não é válido.")
            .MaximumLength(450).WithMessage("O email deve ter no máximo 450 caracteres.");
    }
}
