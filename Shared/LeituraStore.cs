using System.Text.Json;
using VirtualSensor.Common;

namespace VirtualSensor.Shared;

/// <summary>
/// Contrato de troca de dado entre o Sensor (Worker Service) e a Platform
/// (API): o Sensor escreve a leitura de cada ciclo aqui, a Platform lê
/// pra calcular o risco. É a única coisa que liga os dois processos —
/// nenhuma chamada direta entre eles, só esse arquivo compartilhado.
/// </summary>
public sealed record SnapshotLeituras(DateTimeOffset Ciclo, Dictionary<string, decimal> ChuvaPorCelula);

public static class LeituraStore
{
    private static string CaminhoArquivo() =>
        Path.Combine(PathHelpers.PastaCompartilhada("estado-sensor"), "leituras_atual.json");

    public static void Salvar(SnapshotLeituras snapshot)
    {
        var dir = PathHelpers.PastaCompartilhada("estado-sensor");
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(new
        {
            ciclo = snapshot.Ciclo.ToString("o"),
            chuva_por_celula = snapshot.ChuvaPorCelula,
        });

        // Escreve num arquivo temporário e renomeia por cima do final:
        // evita a Platform ler um arquivo pela metade se os dois
        // processos baterem no mesmo instante.
        var caminho = CaminhoArquivo();
        var temp = caminho + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, caminho, overwrite: true);
    }

    public static SnapshotLeituras? Carregar()
    {
        var caminho = CaminhoArquivo();
        if (!File.Exists(caminho))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(caminho));
        var root = doc.RootElement;

        var ciclo = DateTimeOffset.Parse(root.GetProperty("ciclo").GetString()!);
        var chuva = new Dictionary<string, decimal>();
        foreach (var prop in root.GetProperty("chuva_por_celula").EnumerateObject())
        {
            chuva[prop.Name] = prop.Value.GetDecimal();
        }

        return new SnapshotLeituras(ciclo, chuva);
    }
}
