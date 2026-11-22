using System.Text.Json;
using VirtualSensor.Common;

namespace VirtualSensor.Processing;

/// <summary>
/// Camada de aplicação: fator de maré (M).
///
/// A DHN não tem API de maré — só publica uma tábua anual em PDF. O PDF
/// oficial de 2026 pro Porto do Recife (16 componentes harmônicas, nível
/// médio 1.28 m) foi extraído uma vez pra Data/mare_recife_2026.json
/// (lista de extremos preamar/baixa-mar do ano inteiro). Maré é
/// astronômica e previsível o ano todo, então não tem "tempo real" pra
/// buscar aqui: o cálculo é interpolação por cosseno entre os dois
/// extremos que cercam o instante consultado.
///
/// M: 1 enquanto a maré prevista está no nível médio ou abaixo, crescendo
/// dali até ~2 no pico da preamar (ver FatorMare pra detalhe da fórmula e
/// a simplificação assumida). Aplicado igual pra toda a RMR por enquanto
/// (sem diferenciar células perto de canal/rio ainda — ver README,
/// próximas fases).
/// </summary>
public static class TideCalculator
{
    private sealed record TabuaDeMares(List<(DateTimeOffset Ts, decimal AlturaM)> Extremos, decimal NivelMedioM);

    // Lazy<T> com isThreadSafe:true garante que Carregar() roda uma única
    // vez mesmo se duas requisições chamarem AlturaPrevistaM/FatorMare ao
    // mesmo tempo (relevante assim que isto virar API com requests
    // concorrentes, ao contrário do loop single-thread de hoje).
    private static readonly Lazy<TabuaDeMares> _tabua = new(Carregar, isThreadSafe: true);

    private static TabuaDeMares Carregar()
    {
        var marePath = Path.Combine(PathHelpers.SiblingDir("Data"), "mare_recife_2026.json");
        using var stream = File.OpenRead(marePath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var nivelMedioM = root.GetProperty("nivel_medio_m").GetDecimal();

        var extremos = new List<(DateTimeOffset, decimal)>();
        foreach (var e in root.GetProperty("extremos").EnumerateArray())
        {
            var ts = DateTimeOffset.Parse(e.GetProperty("datetime").GetString()!);
            var altura = e.GetProperty("altura_m").GetDecimal();
            extremos.Add((ts, altura));
        }
        return new TabuaDeMares(extremos, nivelMedioM);
    }

    public static decimal AlturaPrevistaM(DateTimeOffset instante)
    {
        var extremos = _tabua.Value.Extremos;

        var idx = extremos.FindIndex(e => e.Ts > instante);
        if (idx <= 0 || idx >= extremos.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(instante), $"Instante {instante} fora da tábua de marés carregada (só cobre 2026)");
        }

        var (t0, h0) = extremos[idx - 1];
        var (t1, h1) = extremos[idx];

        var fracao = (double)(instante - t0).Ticks / (t1 - t0).Ticks;
        var cosseno = (decimal)((1 - Math.Cos(Math.PI * fracao)) / 2);
        return h0 + (h1 - h0) * cosseno;
    }

    public static decimal FatorMare(DateTimeOffset instante)
    {
        var altura = AlturaPrevistaM(instante);
        var nivelMedioM = _tabua.Value.NivelMedioM;
        // Nível médio como referência E escala: M=1 pra toda a metade
        // inferior da faixa de maré (não só maré baixa "de fato"), e cresce
        // dali até ~2 no pico de preamar. Simplificação por falta de uma
        // cota de referência calibrada (o documento original não define
        // qual seria); ver README.
        return 1.0m + Math.Max(0.0m, (altura - nivelMedioM) / nivelMedioM);
    }
}
