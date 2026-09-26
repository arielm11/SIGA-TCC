using FluentValidation;
using TccManager.Shared.DTOs;
using TccManager.Shared.Validation;

namespace TccManager.Api.Validators;

/// <summary>
/// Issue #88 (D9): valida o corpo de <c>POST /api/auth/trocar-senha-obrigatoria</c>. A regra
/// de negócio "SenhaAtual precisa autenticar" e "PrecisaTrocarSenha precisa estar ligada" são
/// checadas no controller (dependem de acesso a banco); aqui só a forma do payload e a
/// política de senha da NovaSenha — delega para <see cref="PoliticaSenha"/>, a mesma função
/// usada pelo bootstrap, para que a senha nova nunca possa ser mais fraca que a temporária que
/// ela substitui.
/// </summary>
public class TrocarSenhaObrigatoriaDtoValidator : AbstractValidator<TrocarSenhaObrigatoriaDto>
{
    public TrocarSenhaObrigatoriaDtoValidator()
    {
        RuleFor(dto => dto.Email)
            .NotEmpty().WithMessage("O email é obrigatório.")
            .MaximumLength(450).WithMessage("O email deve ter no máximo 450 caracteres.");

        RuleFor(dto => dto.SenhaAtual)
            .NotEmpty().WithMessage("A senha atual é obrigatória.")
            .MaximumLength(200).WithMessage("A senha atual deve ter no máximo 200 caracteres.");

        RuleFor(dto => dto.NovaSenha)
            .NotEmpty().WithMessage("A nova senha é obrigatória.")
            .Must((dto, novaSenha) => PoliticaSenha.Valida(novaSenha, dto.Email, dto.SenhaAtual, out _))
            .WithMessage((dto, novaSenha) =>
            {
                PoliticaSenha.Valida(novaSenha, dto.Email, dto.SenhaAtual, out var motivo);
                return motivo ?? "A nova senha não atende à política de senha.";
            });
    }
}
