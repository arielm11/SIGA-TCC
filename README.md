# 🎓 SIGA-TCC (TccManager)

[![.NET Version](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
[![Blazor](https://img.shields.io/badge/Frontend-Blazor%20WASM-blue.svg)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![SQL Server](https://img.shields.io/badge/Database-SQL%20Server-red.svg)](https://www.microsoft.com/sql-server/)

O **SIGA-TCC (TccManager)** é uma plataforma web robusta e de nível corporativo projetada para digitalizar, organizar e automatizar todo o ciclo de vida do Trabalho de Conclusão de Curso (TCC) em instituições de ensino superior. 

O sistema substitui processos manuais baseados em planilhas e trocas de e-mails, centralizando a jornada acadêmica em um fluxo estruturado, seguro e auditável para todos os atores envolvidos.

---

## 📚 Documentação Completa (Wiki)

Para evitar redundância e manter um histórico centralizado de desenvolvimento, **toda a documentação técnica, arquitetural e guias de instalação detalhados foram movidos para a Wiki do repositório.**

Acesse os módulos completos através dos links abaixo:

1. 🏠 **[Página Inicial da Wiki](../../wiki)** — Visão geral do escopo do projeto e histórico das 5 Ondas (*Waves*) de desenvolvimento.
2. 🏛️ **[Arquitetura e Modelagem do Banco](../../wiki/Arquitetura-e-Modelagem-do-Banco)** — Divisão dos projetos da solução (.NET 9 + Blazor WASM) e o dicionário de dados do SQL Server (Entity Framework Core).
3. 📋 **[Requisitos e Regras de Negócio](../../wiki/Requisitos-e-Regras-de-Negócio)** — Mapeamento completo dos Requisitos Funcionais (RFs) por perfil e as travas de validação severas (como a RN03 de Aceite Final e a RN05 de composição mínima da banca).
4. 🛠️ **[Guia de Instalação e Configuração](../../wiki/Guia-de-Instalação-e-Configuração)** — Passo a passo para clonar o repositório, configurar a *Connection String*, rodar as *Migrations* e executar o script de *Seed* SQL para o primeiro acesso.

---

## 🏗️ Arquitetura Resumida

A solução foi desenvolvida seguindo o padrão de separação de responsabilidades com forte tipagem de dados, dividida em três camadas principais:

| Projeto | Tecnologia | Função Principal |
| :--- | :--- | :--- |
| **`TccManager.Api`** | ASP.NET Core Web API (.NET 9) | Endpoint REST, regras de negócio, segurança (JWT) e persistência. |
| **`TccManager.Client`**| Blazor WebAssembly (WASM) | Frontend SPA (Single Page Application) responsivo com Bootstrap. |
| **`TccManager.Shared`**| C# Class Library | Entidades de domínio (Models), DTOs e Enums compartilhados. |

---

## 👥 Fluxo de Trabalho por Perfis

O sistema orquestra nativamente a interação entre 4 papéis essenciais protegidos por regras de autorização (*Role-Based Authorization*):

* **Aluno:** Submete propostas de temas, realiza o upload das entregas parciais/finais e acompanha as informações geradas para o dia da sua defesa.
* **Orientador:** Avalia as solicitações de orientação, registra atas de reuniões periódicas (*Acompanhamentos*) e emite o parecer de "Aceite Final".
* **Coordenador:** Monitora o Dashboard de KPIs da instituição, designa orientadores, gerencia o cadastro de membros externos do mercado, agenda bancas e consolida o resultado final anexando a ata assinada em PDF.
* **Membro da Banca (Avaliador):** Visualiza de forma isolada os cards das bancas para as quais foi escalado e faz o download direto do arquivo final do TCC para avaliação prévia.

---

## ⚡ Inicialização Rápida (Sumário)

Caso queira subir o projeto imediatamente em seu ambiente de desenvolvimento, siga o fluxo básico:

1. Certifique-se de ter instalado o **.NET 9 SDK** e o **SQL Server**.
2. Configure a `ConnectionString` dentro do arquivo `TccManager.Api/appsettings.json`.
3. Execute o comando `Update-Database -Project TccManager.Api` via Console do Gerenciador de Pacotes para criar a estrutura de tabelas.
4. Rode o script contido no **[Guia de Configuração da Wiki](../../wiki/Guia-de-Instalação-e-Configuração)** no seu banco SQL para injetar as contas iniciais de Coordenador e Professor.
5. Configure a solução para iniciar múltiplos projetos simultaneamente (`Api` + `Client`) e pressione `F5`.

---
*Para dúvidas profundas sobre regras de validação ou endpoints específicos, consulte a aba de **[Wiki](../../wiki)** do projeto.*
