using VirtualSensor.Grid;

namespace VirtualSensor.Sensing;

/// <summary>
/// Associa cada leitura de estação à célula H3 que a contém. Uma célula
/// pode receber mais de uma leitura; nesse caso usamos o máximo, por
/// segurança (não subestimar).
///
/// Isso ainda é sensoriamento, não interpretação: cada célula/sensor
/// reporta a chuva bruta (chuva_mm) da estação mais próxima dela, sem
/// calcular nível de risco. Classificar isso em níveis é regra de
/// negócio — fica pra uma camada de interpretação separada.
/// </summary>
public static class CellReadingAssigner
{
    public static Dictionary<string, decimal> AtribuirLeiturasACelulas(
        IEnumerable<Leitura> leituras,
        Dictionary<string, CellInfo> grid)
    {
        var chuvaPorCelula = new Dictionary<string, decimal>();
        foreach (var leitura in leituras)
        {
            var cell = H3GridBuilder.LatLngToCellKey(leitura.Lat, leitura.Lng);
            if (!grid.ContainsKey(cell))
            {
                // Estação cai fora da grade da RMR (ex.: perto da borda); ignora.
                continue;
            }

            chuvaPorCelula[cell] = chuvaPorCelula.TryGetValue(cell, out var atual)
                ? Math.Max(atual, leitura.ChuvaMm)
                : leitura.ChuvaMm;
        }
        return chuvaPorCelula;
    }
}
