namespace TccManager.Client.Services;

/// <summary>
/// Carrega apenas o e-mail entre <c>Login.razor</c> e <c>TrocarSenha.razor</c> quando o login
/// retorna 403 (troca de senha obrigatória — issue #88, D8/D10). Deliberadamente NÃO carrega a
/// senha temporária: ela é redigitada na página nova, para não existir nenhum lugar do cliente
/// (nem em memória entre componentes) guardando uma senha em claro por mais tempo que o
/// estritamente necessário para o POST.
///
/// Serviço Scoped: sobrevive a navegações internas (SPA) dentro da mesma sessão do WASM, mas é
/// perdido em F5/navegação direta — por isso o campo de e-mail em TrocarSenha.razor precisa
/// continuar editável mesmo quando este estado está vazio.
/// </summary>
public class EstadoTrocaSenha
{
    public string? Email { get; set; }
}
