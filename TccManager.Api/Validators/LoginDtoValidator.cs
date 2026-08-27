using FluentValidation;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class LoginDtoValidator : AbstractValidator<LoginDto>
{
    public LoginDtoValidator()
    {
        // Issue #73 (achado A07-1 da revisão de segurança): LoginDto não tinha nenhuma
        // validação — é o único endpoint [AllowAnonymous] que recebe texto livre. Nem Email
        // nem Senha passam por _sanitizerService.Sanitizar (não são persistidos como texto
        // livre), então os limites aqui medem o valor cru mesmo. Email alinhado ao teto de
        // Usuario.Email ([MaxLength(450)]); Senha não tem coluna correspondente (só chega ao
        // BCrypt.Verify, que trunca em 72 bytes e tem custo constante independente do
        // tamanho de entrada — não é um vetor de custo computacional), mas um teto razoável
        // evita payload de autenticação desproporcional ao caso de uso.
        RuleFor(dto => dto.Email)
            .NotEmpty().WithMessage("O email é obrigatório.")
            .MaximumLength(450).WithMessage("O email deve ter no máximo 450 caracteres.");

        RuleFor(dto => dto.Senha)
            .NotEmpty().WithMessage("A senha é obrigatória.")
            .MaximumLength(200).WithMessage("A senha deve ter no máximo 200 caracteres.");
    }
}
