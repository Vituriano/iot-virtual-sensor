using System.Text.Json;

namespace VirtualSensor.Shared;

/// <summary>
/// Leitura de um sensor virtual (uma célula H3) num instante. Cada sensor
/// publica a sua, de forma independente — não existe "ciclo" no contrato:
/// a Platform trata cada mensagem pelo timestamp que ela traz.
/// </summary>
public sealed record LeituraCelula(string H3, DateTimeOffset Ts, decimal ChuvaMm);

/// <summary>
/// Contrato MQTT entre o Sensor (publisher) e a Platform (subscriber):
/// cada sensor publica no seu próprio tópico <c>rmr/chuva/{h3}</c>, com
/// retain — uma Platform que (re)conecta recebe a última leitura de cada
/// sensor na hora, em vez de esperar a próxima. É a única coisa que liga
/// os dois processos.
/// </summary>
public static class LeituraMqtt
{
    private const string Prefixo = "rmr/chuva/";

    /// <summary>Filtro que a Platform assina: todos os sensores.</summary>
    public const string FiltroTodos = Prefixo + "+";

    public static string TopicoDe(string h3) => Prefixo + h3;

    public static string? H3De(string topico) =>
        topico.StartsWith(Prefixo, StringComparison.Ordinal) ? topico[Prefixo.Length..] : null;

    public static byte[] Serializar(LeituraCelula leitura) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            ts = leitura.Ts.ToString("o"),
            chuva_mm = leitura.ChuvaMm,
        });

    public static LeituraCelula Desserializar(string h3, ReadOnlySpan<byte> payload)
    {
        using var doc = JsonDocument.Parse(payload.ToArray());
        var root = doc.RootElement;
        return new LeituraCelula(
            h3,
            DateTimeOffset.Parse(root.GetProperty("ts").GetString()!),
            root.GetProperty("chuva_mm").GetDecimal());
    }
}
