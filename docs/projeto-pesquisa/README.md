# Projeto de Pesquisa — SIGA-TCC

Documentação acadêmica do TCC (Trabalho de Conclusão de Curso) do projeto
SIGA-TCC / TccManager, seguindo o template ABNT do Centro Universitário de
Itajubá-FEPI.

Esta pasta contém primeiro o **Projeto de Pesquisa** (pré-projeto exigido
antes do TCC em si). O TCC completo será estruturado depois, em uma pasta
separada (`docs/tcc/`), reaproveitando o mesmo layout/estilo.

## Estrutura

```
docs/projeto-pesquisa/
├── main.tex                          # documento principal (junta tudo)
├── referencias.bib                   # referências bibliográficas (BibTeX)
├── capitulos/
│   ├── 01-apresentacao-tema.tex
│   ├── 02-justificativa.tex
│   ├── 03-problema-pesquisa.tex
│   ├── 04-objetivos.tex
│   ├── 05-hipoteses.tex
│   ├── 06-referencial-teorico.tex
│   ├── 07-procedimentos-metodologicos.tex
│   └── 08-cronograma.tex
└── build/
    └── main.pdf                      # PDF gerado automaticamente pela Action
```

## Template institucional

O layout (capa, folha de rosto, margens, fonte, numeração de página) foi
reproduzido a partir do arquivo `.doc` fornecido pela faculdade, que está
preservado em `docs/templates/template-projeto-pesquisa-fepi.doc` para
referência. Detalhes conferidos diretamente nesse arquivo:

- Margens: 3 cm esquerda/direita, 2,5 cm superior/inferior (papel A4).
- Fonte do corpo do texto: Arial 12 (aqui usamos o pacote `helvet`, que é
  metricamente compatível).
- Numeração de página no canto superior direito, começando na folha de
  rosto (a capa não é numerada).

## Como compilar

### Automaticamente (GitHub Actions)

Todo push que altere algo em `docs/projeto-pesquisa/` dispara a Action
`.github/workflows/build-latex.yml`, que compila o PDF e faz commit dele
de volta em `docs/projeto-pesquisa/build/main.pdf`.

### Localmente

Requer uma instalação de TeX Live com a classe `abntex2` e o pacote
`abntex2cite` (ambos disponíveis nas distribuições `texlive-full` /
`texlive-lang-portuguese` + `texlive-bibtex-extra`).

```bash
cd docs/projeto-pesquisa
latexmk -pdf -interaction=nonstopmode main.tex
```

O PDF final é gerado em `docs/projeto-pesquisa/main.pdf`.

## Citações (ABNT)

As referências ficam em `referencias.bib` (formato BibTeX). No texto, cite
com `\citeonline{chave}` ou `\cite{chave}`, onde `chave` é o identificador
que você escolher para a entrada.
