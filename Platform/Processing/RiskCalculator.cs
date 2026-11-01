using VirtualSensor.Grid;

namespace VirtualSensor.Processing;

public sealed record RiscoCelula(
    string Municipio,
    decimal Chuva15MinMm,
    decimal Chuva1HMm,
    decimal Chuva3HMm,
    decimal P,
    decimal M,
    decimal S,
    decimal R,
    int Nivel,
    decimal Probabilidade,
    string Confianca);

/// <summary>
/// Combina chuva, maré e terreno num nível de risco por célula.
///
/// Fator de chuva (P) usando acumulados de 15min/1h/3h (ver
/// RainHistoryStore), fator de maré (M, ver TideCalculator) aplicado
/// igual pra toda a RMR, e agora fator de terreno/suscetibilidade (S, ver
/// TerrainCalculator) — elevação, declividade e distância a canal por
/// célula. Fórmula completa do documento original.
///
/// R_cell = P · M · S
/// P = maior entre (acumulado_15min/limiar_15min, acumulado_1h/limiar_1h, acumulado_3h/limiar_3h)
/// Níveis: 0 (sem risco), 1 (atenção), 2 (alto), 3 (muito alto).
///
/// Como S agora normalmente é bem menor que 1 (é uma média de três fatores
/// 0-1, raramente todos no máximo ao mesmo tempo), R fica sistematicamente
/// menor que nas versões anteriores para a mesma chuva/maré — os limiares
/// abaixo precisam de recalibração contra eventos reais mais do que nunca.
///
/// Também calcula `Probabilidade` e `Confianca`, campos que o documento
/// original pede na saída por célula e que não existiam até agora:
///   - Probabilidade: versão contínua do nível discreto (interpola R pelos
///     mesmos NivelCortes) — NÃO é uma probabilidade estatística calibrada
///     contra ocorrências reais, só um proxy enquanto essa calibração não
///     existe.
///   - Confianca: "alta" se a célula teve leitura de chuva neste ciclo,
///     "média" se só tem leitura mais antiga ainda dentro da janela de 3h.
///     O documento também liga confiança à distância até o pluviômetro
///     mais próximo e à disponibilidade de rótulos de ocorrência por
///     região — nenhuma das duas entra aqui ainda, então não existe tier
///     "baixa" nesta versão.
/// </summary>
public static class RiskCalculator
{
    // Limiares por janela, em mm. Ajustados à mão por enquanto; na
    // calibração (ver documento) isso vira algo aprendido a partir dos
    // eventos históricos de alagamento.
    private const decimal Limiar15MinMm = 10.0m;
    private const decimal Limiar1HMm = 30.0m;
    private const decimal Limiar3HMm = 60.0m;

    // Suscetibilidade padrão quando a célula não tem elevação conhecida
    // (não deveria acontecer — a grade e a elevação vêm da mesma fonte —
    // mas serve de rede de segurança): nem o melhor nem o pior caso.
    private const decimal SuscetibilidadePadrao = 0.5m;

    private static readonly decimal[] NivelCortes = { 0.3m, 0.7m, 1.2m };

    // Abaixo de que idade uma leitura ainda conta como "fresca" (confiança
    // alta) em vez de "só dentro da janela de 3h" (confiança média).
    private static readonly TimeSpan IdadeConfiancaAlta = TimeSpan.FromMinutes(20);

    public static int NivelDeRisco(decimal r)
    {
        if (r < NivelCortes[0]) return 0;
        if (r < NivelCortes[1]) return 1;
        if (r < NivelCortes[2]) return 2;
        return 3;
    }

    /// <summary>
    /// Interpola R através dos mesmos cortes usados pro nível, dando uma
    /// versão contínua do nível discreto (0 no corte mais baixo, 1 no mais
    /// alto). Não é uma probabilidade calibrada — ver comentário da classe.
    /// </summary>
    public static decimal CalcularProbabilidade(decimal r)
    {
        if (r <= 0m) return 0m;
        if (r < NivelCortes[0]) return Interpolar(r, 0m, NivelCortes[0], 0m, 1m / 3m);
        if (r < NivelCortes[1]) return Interpolar(r, NivelCortes[0], NivelCortes[1], 1m / 3m, 2m / 3m);
        if (r < NivelCortes[2]) return Interpolar(r, NivelCortes[1], NivelCortes[2], 2m / 3m, 1m);
        return 1m;
    }

    private static decimal Interpolar(decimal x, decimal x0, decimal x1, decimal y0, decimal y1)
        => y0 + (y1 - y0) * (x - x0) / (x1 - x0);

    public static string CalcularConfianca(DateTimeOffset tsAgora, DateTimeOffset ultimaLeituraTs)
        => tsAgora - ultimaLeituraTs <= IdadeConfiancaAlta ? "alta" : "média";

    public static decimal FatorChuva(AcumuladoCelula acumulado)
    {
        var p15 = acumulado.Min15 / Limiar15MinMm;
        var p1h = acumulado.Hora1 / Limiar1HMm;
        var p3h = acumulado.Hora3 / Limiar3HMm;
        return Math.Max(p15, Math.Max(p1h, p3h));
    }

    public static Dictionary<string, RiscoCelula> CalcularRisco(
        Dictionary<string, CellInfo> grid,
        Dictionary<string, AcumuladoCelula> acumuladoPorCelula,
        decimal fatorMareAtual,
        Dictionary<string, decimal> suscetibilidadePorCelula,
        DateTimeOffset tsAgora)
    {
        var resultado = new Dictionary<string, RiscoCelula>();
        foreach (var (cell, acumulado) in acumuladoPorCelula)
        {
            var info = grid[cell];
            var p = FatorChuva(acumulado);
            var s = suscetibilidadePorCelula.GetValueOrDefault(cell, SuscetibilidadePadrao);
            var r = p * fatorMareAtual * s;
            resultado[cell] = new RiscoCelula(
                info.Municipio,
                acumulado.Min15,
                acumulado.Hora1,
                acumulado.Hora3,
                p,
                fatorMareAtual,
                s,
                r,
                NivelDeRisco(r),
                CalcularProbabilidade(r),
                CalcularConfianca(tsAgora, acumulado.UltimaLeituraTs));
        }
        return resultado;
    }
}
