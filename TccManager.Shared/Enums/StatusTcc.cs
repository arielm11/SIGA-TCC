namespace TccManager.Shared.Enums;

// Issue #82 — ver docs/arquitetura/2026-08-30-status-tcc-emandamento-nao-usado.md, D1 (seção 3):
// os valores/inteiros não mudam (armazenados como int puro, sem HasConversion — não
// renumerar), só os comentários abaixo, para fixar a semântica no próprio enum e evitar a
// recorrência do achado original (dois grupos de código discordando silenciosamente sobre o
// que Aprovado/EmAndamento significavam).
public enum StatusTcc
{
    /// <summary>Proposta submetida, aguardando decisão do Coordenador.</summary>
    Pendente = 0,

    /// <summary>Orientador designado; o aluno AINDA NÃO enviou nenhuma entrega.</summary>
    Aprovado = 1,

    /// <summary>Proposta rejeitada OU reprovado na banca.</summary>
    Reprovado = 2,

    /// <summary>O aluno já enviou pelo menos uma entrega (qualquer <see cref="TipoEntrega"/>).</summary>
    EmAndamento = 3,

    /// <summary>Aceite final concedido pelo orientador.</summary>
    AguardandoDefesa = 4,

    /// <summary>Resultado da banca registrado.</summary>
    Finalizado = 5
}
