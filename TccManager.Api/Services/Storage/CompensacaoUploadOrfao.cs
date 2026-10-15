namespace TccManager.Api.Services.Storage;

/// <summary>
/// Issue #105 (achado da revisão de #73): a compensação de upload órfão — apagar um arquivo já
/// gravado em disco quando o <c>SaveChangesAsync</c> que o persistiria falha, já que não existe
/// transação distribuída entre disco e banco — só existia em <c>TccController.EnviarEntrega</c>
/// (issue #69, item 4). <c>CoordenadorController.RegistrarResultadoBanca</c> tinha o mesmo risco
/// para o upload da ata sem nenhuma compensação. Extraído aqui para as duas chamadas
/// compartilharem a mesma lógica de "apagar e logar o resultado", best-effort — a falha ao
/// remover o órfão nunca pode mascarar o erro original que disparou a compensação.
/// </summary>
public static class CompensacaoUploadOrfao
{
    /// <param name="storageService">serviço de storage usado para apagar o arquivo.</param>
    /// <param name="caminho">caminho relativo persistido pelo <see cref="IStorageService.UploadAsync"/> original.</param>
    /// <param name="logSucesso">
    /// chamado se a remoção funcionar — cada chamador loga com suas próprias propriedades
    /// estruturadas (ex.: TccId vs BancaId), por isso é um delegate em vez de um template fixo.
    /// </param>
    /// <param name="logFalha">chamado se a própria remoção falhar (best-effort: nunca propaga).</param>
    public static async Task ExecutarAsync(
        IStorageService storageService,
        string caminho,
        Action logSucesso,
        Action logFalha)
    {
        try
        {
            await storageService.DeleteAsync(caminho);
            logSucesso();
        }
        catch (Exception)
        {
            logFalha();
        }
    }
}
