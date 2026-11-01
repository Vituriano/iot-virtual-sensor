using System.Text.Json;

namespace VirtualSensor.Shared;

/// <summary>
/// Contrato de troca de dado entre o Sensor (publisher) e a Platform
/// (subscriber) sobre MQTT: o Sensor publica a chuva de cada ciclo no
/// tópico <see cref="TopicoCiclo"/>, a Platform assina e calcula o risco.
/// É a única coisa que liga os dois processos — nenhuma chamada direta,
/// só mensagens via broker.
/// </summary>
public sealed record SnapshotLeituras(DateTimeOffset Ciclo, Dictionary<string, decimal> ChuvaPorCelula);

public static class LeituraMqtt
{
    /// <summary>
    /// Uma mensagem por ciclo (a cada 15 min) com a chuva de todas as
    /// células. Publicada com retain, pra uma Platform que (re)conecta
    /// receber o último ciclo na hora em vez de esperar o próximo.
    /// </summary>
    public const string TopicoCiclo = "rmr/chuva/ciclo";

    public static byte[] Serializar(SnapshotLeituras snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            ciclo = snapshot.Ciclo.ToString("o"),
            chuva_por_celula = snapshot.ChuvaPorCelula,
        });

    public static SnapshotLeituras Desserializar(ReadOnlySpan<byte> payload)
    {
        using var doc = JsonDocument.Parse(payload.ToArray());
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
