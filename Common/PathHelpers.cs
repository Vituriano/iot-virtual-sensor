using System.Runtime.CompilerServices;

namespace VirtualSensor.Common;

/// <summary>
/// Resolve uma pasta irmã do diretório de quem chama. Usado por
/// H3GridBuilder pra achar Data/. [CallerFilePath] captura o arquivo de
/// quem chamou (não deste arquivo), então funciona igual não importa de
/// qual pasta venha a chamada.
/// </summary>
public static class PathHelpers
{
    public static string SiblingDir(string subpasta, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", subpasta));
    }
}
