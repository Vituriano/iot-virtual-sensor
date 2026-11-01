using System.Runtime.CompilerServices;

namespace VirtualSensor.Common;

/// <summary>
/// Resolve uma pasta irmã do diretório de quem chama. Usado por
/// H3GridBuilder pra achar Data/, e por RainHistoryStore pra achar seu
/// estado/ interno. [CallerFilePath] captura o arquivo de quem chamou
/// (não deste arquivo), então funciona igual não importa de qual pasta
/// venha a chamada.
/// </summary>
public static class PathHelpers
{
    public static string SiblingDir(string subpasta, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", subpasta));
    }

    /// <summary>
    /// Pasta compartilhada entre Sensor e Platform — dois projetos/processos
    /// diferentes que precisam concordar no mesmo caminho pra trocar dado
    /// (o Sensor escreve a leitura atual, a Platform lê). Ancorada na raiz
    /// do repositório (onde este arquivo mora, dentro de Shared/), não no
    /// projeto de quem chama, por isso não usa [CallerFilePath] direto:
    /// RaizDoRepositorio() é chamada sem argumento dentro desta mesma
    /// classe, então "here" sempre captura o caminho deste arquivo.
    /// </summary>
    public static string PastaCompartilhada(string subpasta) =>
        Path.Combine(RaizDoRepositorio(), subpasta);

    private static string RaizDoRepositorio([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here)!; // Shared/Common
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }
}
