# Configuração do ambiente local (Linux e Windows)

Checklist para rodar o SIGA-TCC (`TccManager.Api` + `TccManager.Client`) do zero em uma
máquina nova. Complementa o resumo do `README.md` — aqui está o passo a passo completo,
já validado em Linux, com as diferenças pontuais para Windows.

## 1. Pré-requisitos

- **.NET 9 SDK**.
- **`dotnet-ef`** (ferramenta global, não vem com o SDK):
  ```bash
  dotnet tool install --global dotnet-ef
  ```
- **SQL Server 2022**, de uma destas formas:
  - **Windows**: instância local (nativa), autenticação do Windows funciona sem configuração extra.
  - **Linux/macOS**: via Docker — branch `chore/docker-compose-sqlserver` traz `docker-compose.yml` +
    `.env.example`. Copie para `.env`, defina `MSSQL_SA_PASSWORD` e rode `docker compose up -d`.

## 2. Connection string e segredos (`TccManager.Api`)

Nunca vão para o repositório — configurar via *user secrets*, por máquina:

```bash
cd TccManager.Api
dotnet user-secrets set "Jwt:Key" "<pelo menos 32 caracteres aleatórios>"
```

Connection string, conforme o SQL Server escolhido:

- **Windows, autenticação do Windows** (instância local):
  ```bash
  dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=TccManager;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  ```
- **Linux/Docker (ou SQL auth em geral)**:
  ```bash
  dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=TccManager;User Id=<usuario>;Password=<senha>;TrustServerCertificate=True;MultipleActiveResultSets=true"
  ```

## 3. Migrations

```bash
dotnet ef database update --project TccManager.Api
```
(ou `Update-Database -Project TccManager.Api` no Console do Gerenciador de Pacotes do Visual Studio)

## 4. Certificado HTTPS de desenvolvimento

O `TccManager.Client` chama a API em `https://localhost:7146` (fixo em
`TccManager.Client/wwwroot/appsettings.json`) — o navegador precisa confiar no certificado de
desenvolvimento, senão as chamadas da SPA para a API falham silenciosamente.

```bash
dotnet dev-certs https --trust
```

- **Windows**: normalmente resolve na hora (usa o certificate store do próprio SO); no Visual
  Studio, o primeiro F5 costuma perguntar e resolver sozinho.
- **Linux**: pode faltar o pacote `libnss3-tools` (usado por Chrome/Firefox via NSS) e a variável
  `SSL_CERT_DIR` (usada por curl/OpenSSL). Se `dotnet dev-certs https --check --trust` não voltar
  "trusted", rode:
  ```bash
  sudo apt install libnss3-tools
  dotnet dev-certs https --clean && dotnet dev-certs https --trust
  echo 'export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:/usr/lib/ssl/certs"' >> ~/.bashrc
  ```
  e abra um shell novo (ou `source ~/.bashrc`) antes de rodar a API.

## 5. Dados de teste (opcional)

`scripts/seed-dados-teste.sql` cria um usuário de cada perfil e um cenário completo de TCC
(proposta pendente + TCC finalizado com banca) — ver instruções e senha no cabeçalho do próprio
arquivo. Idempotente: pode rodar de novo em qualquer máquina sem duplicar dados.

## 6. Validar o ambiente

```bash
dotnet build TccManager.sln
dotnet test
```

## 7. Rodar a aplicação

- `TccManager.Api` — perfil `https` (porta 7146), é o que o Client espera por padrão.
- `TccManager.Client` — perfil `https` (porta 7249).

No Visual Studio: configurar múltiplos projetos de inicialização (`Api` + `Client`) e pressionar F5.
Via CLI, em dois terminais:
```bash
dotnet run --project TccManager.Api --launch-profile https
dotnet run --project TccManager.Client --launch-profile https
```

## Branches relevantes

- `chore/docker-compose-sqlserver` — SQL Server via Docker para quem não tem instância nativa.
- `chore/seed-dados-teste` — este arquivo + `scripts/seed-dados-teste.sql`.
- `feat/bootstrap-primeiro-admin` (#88) — mecanismo de criação do primeiro Admin via
  `Admin:BootstrapEmail`/`BootstrapSenha`/`BootstrapNome` nos user secrets.
