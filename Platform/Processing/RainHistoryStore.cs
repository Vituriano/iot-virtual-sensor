using System.Text.Json;
using VirtualSensor.Common;

namespace VirtualSensor.Processing;

public sealed record HistoricoRegistro(DateTimeOffset Ts, Dictionary<string, decimal> Chuva);

/// <summary>
/// UltimaLeituraTs é o timestamp do registro mais recente que contribuiu
/// pra essa célula — usado como proxy de confiança (uma leitura fresca
/// deste ciclo é mais confiável que uma só presente pela janela de 3h).
/// </summary>
public sealed record AcumuladoCelula(decimal Min15, decimal Hora1, decimal Hora3, DateTimeOffset UltimaLeituraTs);

/// <summary>
/// Camada de suporte: histórico local de leituras de chuva por célula.
///
/// Guarda um registro por ciclo (timestamp + chuva por célula) em
/// estado/chuva_historico.jsonl pra poder calcular acumulados de
/// 15min/1h/3h, como pede a fórmula do documento original (P = maior
/// entre os acumulados de cada janela, cada um dividido pelo seu limiar).
///
/// Suposição importante: cada leitura do feed representa a chuva "deste
/// ciclo" (delta desde a leitura anterior da estação), não um total
/// acumulado absoluto — é assim que o exemplo do professor trata o campo
/// "chuva" (soma direto, sem subtrair nada). Se a semântica real do
/// campo for outra, os acumulados aqui vão superestimar; precisa validar
/// contra um evento de chuva real antes de confiar nos limiares.
///
/// Rodar o sensor com --once várias vezes seguidas em poucos minutos
/// conta a mesma leitura repetidas vezes nos acumulados — isso é
/// esperado só em teste manual, não no loop de produção (1 ciclo real a
/// cada 15 min).
/// </summary>
public static class RainHistoryStore
{
    private static readonly TimeSpan JanelaMaxima = TimeSpan.FromMinutes(3 * 60 + 15);

    private static readonly (string Nome, int Minutos)[] Janelas =
    {
        ("15min", 15), ("1h", 60), ("3h", 180),
    };

    private static string HistoricoPath() =>
        Path.Combine(PathHelpers.SiblingDir("estado"), "chuva_historico.jsonl");

    private static List<HistoricoRegistro> Ler()
    {
        var path = HistoricoPath();
        if (!File.Exists(path))
        {
            return new List<HistoricoRegistro>();
        }

        var registros = new List<HistoricoRegistro>();
        foreach (var linha in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(linha))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(linha);
            var root = doc.RootElement;
            var ts = DateTimeOffset.Parse(root.GetProperty("ts").GetString()!);
            var chuva = new Dictionary<string, decimal>();
            foreach (var prop in root.GetProperty("chuva").EnumerateObject())
            {
                chuva[prop.Name] = prop.Value.GetDecimal();
            }
            registros.Add(new HistoricoRegistro(ts, chuva));
        }
        return registros;
    }

    /// <summary>
    /// Acrescenta a leitura deste ciclo ao histórico local e descarta
    /// entradas mais velhas que a maior janela usada (3h + margem).
    /// Retorna o histórico já atualizado.
    /// </summary>
    public static List<HistoricoRegistro> RegistrarCiclo(DateTimeOffset tsAgora, Dictionary<string, decimal> chuvaPorCelula)
    {
        Directory.CreateDirectory(PathHelpers.SiblingDir("estado"));

        var registros = Ler().Where(r => tsAgora - r.Ts <= JanelaMaxima).ToList();
        registros.Add(new HistoricoRegistro(tsAgora, new Dictionary<string, decimal>(chuvaPorCelula)));

        using var writer = new StreamWriter(HistoricoPath(), append: false);
        foreach (var r in registros)
        {
            var linha = JsonSerializer.Serialize(new { ts = r.Ts.ToString("o"), chuva = r.Chuva });
            writer.WriteLine(linha);
        }

        return registros;
    }

    /// <summary>
    /// Soma, para cada célula, as leituras registradas dentro de cada
    /// janela (15min, 1h, 3h), e guarda o timestamp da leitura mais
    /// recente de cada célula (usado como proxy de confiança).
    /// </summary>
    public static Dictionary<string, AcumuladoCelula> Acumulados(DateTimeOffset tsAgora, List<HistoricoRegistro> registros)
    {
        var somas = new Dictionary<string, decimal[]>(); // [15min, 1h, 3h]
        var ultimaLeitura = new Dictionary<string, DateTimeOffset>();

        for (var i = 0; i < Janelas.Length; i++)
        {
            var (_, minutos) = Janelas[i];
            var limite = tsAgora - TimeSpan.FromMinutes(minutos);
            foreach (var registro in registros)
            {
                if (registro.Ts < limite)
                {
                    continue;
                }
                foreach (var (cell, mm) in registro.Chuva)
                {
                    if (!somas.TryGetValue(cell, out var arr))
                    {
                        arr = new decimal[3];
                        somas[cell] = arr;
                    }
                    arr[i] += mm;
                }
            }
        }

        foreach (var registro in registros)
        {
            foreach (var cell in registro.Chuva.Keys)
            {
                if (!ultimaLeitura.TryGetValue(cell, out var ts) || registro.Ts > ts)
                {
                    ultimaLeitura[cell] = registro.Ts;
                }
            }
        }

        return somas.ToDictionary(
            kv => kv.Key,
            kv => new AcumuladoCelula(kv.Value[0], kv.Value[1], kv.Value[2], ultimaLeitura[kv.Key]));
    }
}
