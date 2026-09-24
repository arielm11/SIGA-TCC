/*
  Seed de dados para testes manuais — SIGA-TCC (TccManager)
  ============================================================

  Cria usuários de cada perfil e um cenário completo de TCC (proposta pendente +
  TCC finalizado com banca) para testar a aplicação manualmente.

  Senha de TODOS os usuários criados por este script: Teste-Seg-2026!#
  (hash BCrypt gerado com BCrypt.Net-Next, o mesmo pacote usado pela API)

  Todos os e-mails usam o domínio @seed.local — o script é IDEMPOTENTE: pode ser
  reexecutado a qualquer momento, ele apaga (por esse domínio de e-mail) e recria
  os dados de teste, sem afetar usuários/TCCs reais.

  Como executar:
    sqlcmd -S localhost -d TccManager -U <usuario> -P <senha> -C -i scripts/seed-dados-teste.sql
  (ou cole o conteúdo no seu client SQL preferido, apontando para o banco TccManager)

  Usuários criados:
    admin.seed@seed.local        Admin
    coordenador.seed@seed.local  Coordenador
    orientador.seed@seed.local   Professor (orientador do TCC finalizado)
    avaliador.seed@seed.local    Professor (avaliador interno da banca)
    aluno1.seed@seed.local       Aluno — TCC finalizado, com banca e nota
    aluno2.seed@seed.local       Aluno — proposta de TCC ainda Pendente (para
                                  testar a aprovação pelo Coordenador)

  Observação: Banca.AtaCaminho fica vazio (nenhum PDF real foi enviado por este
  script). Isso é esperado — GET /coordenador/banca/{id}/ata-assinada vai
  responder 404 "nenhuma cópia assinada foi anexada", igual ao comportamento
  real da aplicação para uma banca sem upload de ata.
*/

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;

DECLARE @senhaHash NVARCHAR(200) = N'$2a$11$5y01BwbO7KfW3MKGts0h2uMzNgY85VtC.7PLWZfN5mnmHIdNF5giq';

-- 1) Limpeza dos dados de teste de uma execução anterior (idempotência) ------

DELETE ba
FROM BancaAvaliadores ba
INNER JOIN Banca b ON b.Id = ba.BancaId
INNER JOIN Tccs t ON t.Id = b.TccId
INNER JOIN usuarios al ON al.Id = t.AlunoId
WHERE al.Email LIKE '%@seed.local';

DELETE b
FROM Banca b
INNER JOIN Tccs t ON t.Id = b.TccId
INNER JOIN usuarios al ON al.Id = t.AlunoId
WHERE al.Email LIKE '%@seed.local';

DELETE e
FROM Entregas e
INNER JOIN Tccs t ON t.Id = e.TccId
INNER JOIN usuarios al ON al.Id = t.AlunoId
WHERE al.Email LIKE '%@seed.local';

DELETE a
FROM Acompanhamentos a
INNER JOIN Tccs t ON t.Id = a.TccId
INNER JOIN usuarios al ON al.Id = t.AlunoId
WHERE al.Email LIKE '%@seed.local';

DELETE t
FROM Tccs t
INNER JOIN usuarios al ON al.Id = t.AlunoId
WHERE al.Email LIKE '%@seed.local';

DELETE FROM MembrosExternos WHERE Email LIKE '%@seed.local';
DELETE FROM usuarios WHERE Email LIKE '%@seed.local';

-- 2) Usuários -----------------------------------------------------------------
-- Tipo: Aluno=0, Professor=1, Coordenador=2, Admin=3

INSERT INTO usuarios (Nome, Email, SenhaHash, Tipo, Ativo, LimiteOrientandos, AceitandoOrientandos, PrecisaTrocarSenha)
VALUES
    (N'Admin Seed',             N'admin.seed@seed.local',       @senhaHash, 3, 1, 5, 1, 0),
    (N'Coordenador Seed',       N'coordenador.seed@seed.local', @senhaHash, 2, 1, 5, 1, 0),
    (N'Orientador Seed',        N'orientador.seed@seed.local',  @senhaHash, 1, 1, 5, 1, 0),
    (N'Avaliador Interno Seed', N'avaliador.seed@seed.local',   @senhaHash, 1, 1, 5, 1, 0),
    (N'Aluno Seed Finalizado',  N'aluno1.seed@seed.local',      @senhaHash, 0, 1, 5, 1, 0),
    (N'Aluno Seed Pendente',    N'aluno2.seed@seed.local',      @senhaHash, 0, 1, 5, 1, 0);

DECLARE @idCoordenador INT = (SELECT Id FROM usuarios WHERE Email = N'coordenador.seed@seed.local');
DECLARE @idOrientador  INT = (SELECT Id FROM usuarios WHERE Email = N'orientador.seed@seed.local');
DECLARE @idAvaliador   INT = (SELECT Id FROM usuarios WHERE Email = N'avaliador.seed@seed.local');
DECLARE @idAluno1      INT = (SELECT Id FROM usuarios WHERE Email = N'aluno1.seed@seed.local');
DECLARE @idAluno2      INT = (SELECT Id FROM usuarios WHERE Email = N'aluno2.seed@seed.local');

-- 3) Membro externo (avaliador externo da banca) -------------------------------

INSERT INTO MembrosExternos (Nome, Email, Instituicao)
VALUES (N'Membro Externo Seed', N'membro.externo.seed@seed.local', N'Instituto Parceiro Seed');

DECLARE @idMembroExterno INT = (SELECT Id FROM MembrosExternos WHERE Email = N'membro.externo.seed@seed.local');

-- 4) TCC A — proposta Pendente, sem orientador (testa aprovação pelo Coordenador)

-- Status: Pendente=0
INSERT INTO Tccs (Titulo, Resumo, DataCriacao, Status, AlunoId, OrientadorId)
VALUES (
    N'Proposta de TCC Seed — Pendente de Aprovação',
    N'Resumo de exemplo gerado pelo script de seed para testar o fluxo de aprovação de propostas pelo Coordenador.',
    DATEADD(DAY, -3, GETUTCDATE()),
    0,
    @idAluno2,
    NULL
);

-- 5) TCC B — ciclo completo até banca finalizada -------------------------------

-- Status: Finalizado=5
INSERT INTO Tccs (Titulo, Resumo, DataCriacao, Status, AlunoId, OrientadorId)
VALUES (
    N'TCC Seed — Ciclo Completo Finalizado',
    N'Resumo de exemplo gerado pelo script de seed para testar o ciclo completo: orientação, entregas, acompanhamento e banca.',
    DATEADD(DAY, -60, GETUTCDATE()),
    5,
    @idAluno1,
    @idOrientador
);

DECLARE @idTccB INT = (SELECT Id FROM Tccs WHERE Titulo = N'TCC Seed — Ciclo Completo Finalizado' AND AlunoId = @idAluno1);

-- Entregas — Tipo: Parcial=0, Final=1 | Status: Pendente=0, Aprovada=1, Rejeitada=2
INSERT INTO Entregas (Titulo, ArquivoCaminho, DataEnvio, Tipo, Status, Feedback, Nota, TccId)
VALUES
    (N'Entrega Parcial Seed', N'seed/entrega-parcial-seed.pdf', DATEADD(DAY, -40, GETUTCDATE()), 0, 1, N'Entrega parcial aprovada pelo orientador (dado de seed).', NULL, @idTccB),
    (N'Entrega Final Seed',   N'seed/entrega-final-seed.pdf',   DATEADD(DAY, -15, GETUTCDATE()), 1, 1, N'Entrega final aprovada pelo orientador (dado de seed).', 9.50, @idTccB);

-- Acompanhamento (ata de reunião de orientação)
INSERT INTO Acompanhamentos (DataReuniao, Ata, TccId)
VALUES (DATEADD(DAY, -30, GETUTCDATE()), N'Reunião de acompanhamento de exemplo (dado de seed): revisão do capítulo 3 e ajustes na metodologia.', @idTccB);

-- Banca — já com resultado lançado (NotaFinal). AtaCaminho vazio de propósito (ver observação no topo).
INSERT INTO Banca (TccId, DataHora, Local, NotaFinal, AtaCaminho)
VALUES (@idTccB, DATEADD(DAY, -10, GETUTCDATE()), N'Sala 203 - Bloco B (seed)', 9.00, N'');

DECLARE @idBancaB INT = (SELECT Id FROM Banca WHERE TccId = @idTccB);

-- RN05: pelo menos 2 avaliadores além do orientador — 1 professor + 1 membro externo
INSERT INTO BancaAvaliadores (BancaId, ProfessorId, MembroExternoId)
VALUES
    (@idBancaB, @idAvaliador, NULL),
    (@idBancaB, NULL, @idMembroExterno);

COMMIT TRANSACTION;

PRINT N'Seed concluído. Senha de todos os usuários: Teste-Seg-2026!#';
