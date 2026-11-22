using System.Text.Json;
using VirtualSensor.Common;

namespace VirtualSensor.Processing;

/// <summary>
/// UltimaLeituraTs é o timestamp da leitura mais recente dessa célula —
/// usado como proxy de confiança (uma leitura fresca é mais confiável que
/// uma só presente pela janela de 3h).
/// </summary>
public sealed record AcumuladoCelula(decimal Min15, decimal Hora1, decimal Hora3, DateTimeOffset UltimaLeituraTs);

/// <summary>
/// Camada de suporte: histórico local de leituras de chuva, por célula.
///
/// Cada sensor publica de forma independente, então o histórico também é
/// por célula: uma lista de (timestamp, mm) em memória, persistida em
/// estado/chuva_historico.jsonl (uma linha por leitura, só acréscimo) pra
/// sobreviver a reinício. Serve pra calcular acumulados de 15min/1h/3h,
/// como pede a fórmula do documento original (P = maior entre os
/// acumulados de cada janela, cada um dividido pelo seu limiar).
///
/// Suposição importante: cada leitura do feed representa a chuva "desta
/// leitura" (delta desde a anterior da estação), não um total acumulado
/// absoluto — é assim que o exemplo do professor trata o campo "chuva"
/// (soma direto, sem subtrair nada). Se a semântica real do campo for
/// outra, os acumulados aqui vão superestimar; precisa validar contra um
/// evento de chuva real antes de confiar nos limiares.
///
/// Uma leitura (célula, timestamp) já registrada é ignorada: o broker
/// reentrega a última leitura de cada sensor (retain) quando a Platform
/// reinicia, e sem isso a mesma chuva seria somada duas vezes.
/// </summary>
public sealed class RainHistoryStore
{
    private static readonly TimeSpan JanelaMaxima = TimeSpan.FromMinutes(3 * 60 + 15);

    private static readonly int[] JanelasMin = { 15, 60, 180 };

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<(DateTimeOffset Ts, decimal Mm)>> _porCelula = new();
    private readonly string _path = Path.Combine(PathHelpers.SiblingDir("estado"), "chuva_historico.jsonl");

    public RainHistoryStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Carregar();
    }

    /// <summary>
    /// Registra a leitura e devolve o acumulado da célula no instante dela.
    /// Idempotente: uma leitura já registrada não é somada de novo, mas o
    /// acumulado é devolvido igual (a Platform precisa dele pra repopular o
    /// cache depois de reiniciar).
    /// </summary>
    public AcumuladoCelula Registrar(string celula, DateTimeOffset ts, decimal mm)
    {
        lock (_lock)
        {
            if (!_porCelula.TryGetValue(celula, out var leituras))
            {
                leituras = new List<(DateTimeOffset, decimal)>();
                _porCelula[celula] = leituras;
            }

            if (leituras.Any(l => l.Ts == ts))
            {
                return Acumular(leituras, ts);
            }

            leituras.RemoveAll(l => ts - l.Ts > JanelaMaxima);
            leituras.Add((ts, mm));

            File.AppendAllText(_path,
                JsonSerializer.Serialize(new { celula, ts = ts.ToString("o"), mm }) + "\n");

            return Acumular(leituras, ts);
        }
    }

    private static AcumuladoCelula Acumular(List<(DateTimeOffset Ts, decimal Mm)> leituras, DateTimeOffset tsAgora)
    {
        var somas = new decimal[JanelasMin.Length];
        for (var i = 0; i < JanelasMin.Length; i++)
        {
            var limite = tsAgora - TimeSpan.FromMinutes(JanelasMin[i]);
            somas[i] = leituras.Where(l => l.Ts >= limite && l.Ts <= tsAgora).Sum(l => l.Mm);
        }

        return new AcumuladoCelula(somas[0], somas[1], somas[2], leituras.Max(l => l.Ts));
    }

    // Lê o arquivo, descarta o que já saiu da janela e reescreve só o que
    // sobrou, pra ele não crescer pra sempre.
    private void Carregar()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var agora = DateTimeOffset.UtcNow;
        foreach (var linha in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(linha))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(linha);
                var root = doc.RootElement;
                var ts = DateTimeOffset.Parse(root.GetProperty("ts").GetString()!);
                if (agora - ts > JanelaMaxima)
                {
                    continue;
                }

                var celula = root.GetProperty("celula").GetString()!;
                if (!_porCelula.TryGetValue(celula, out var leituras))
                {
                    leituras = new List<(DateTimeOffset, decimal)>();
                    _porCelula[celula] = leituras;
                }
                leituras.Add((ts, root.GetProperty("mm").GetDecimal()));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                // Linha em formato antigo ou truncada: ignora em vez de
                // impedir a Platform de subir.
            }
        }

        File.WriteAllLines(_path, _porCelula.SelectMany(kv => kv.Value.Select(l =>
            JsonSerializer.Serialize(new { celula = kv.Key, ts = l.Ts.ToString("o"), mm = l.Mm }))));
    }
}
