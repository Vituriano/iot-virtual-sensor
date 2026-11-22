using VirtualSensor.Grid;
using VirtualSensor.Processing;
using VirtualSensor.Sensing;

namespace VirtualSensor.Tests.Unit;

/// <summary>Grade H3, atribuição de estação a célula, maré e tempestade simulada.</summary>
public class SensorTests
{
    private static readonly Dictionary<string, CellInfo> Grid = H3GridBuilder.BuildGrid();

    // Marco Zero, Recife.
    private static readonly string CelulaRecife = H3GridBuilder.LatLngToCellKey(-8.063m, -34.871m);

    [Fact]
    public void Grade_cobre_os_14_municipios_com_3210_celulas()
    {
        Assert.Equal(3210, Grid.Count);
        Assert.Equal(14, Grid.Values.Select(c => c.Municipio).Distinct().Count());
        Assert.Equal("Recife", Grid[CelulaRecife].Municipio);
    }

    [Fact]
    public void Duas_estacoes_na_mesma_celula_usam_o_maior_valor()
    {
        var leituras = new[]
        {
            new Leitura(-8.063m, -34.871m, "Recife", 3m, "A"),
            new Leitura(-8.063m, -34.871m, "Recife", 7m, "B"),
        };

        var chuva = CellReadingAssigner.AtribuirLeiturasACelulas(leituras, Grid);

        Assert.Equal(7m, chuva[CelulaRecife]);
    }

    [Fact]
    public void Estacao_fora_da_RMR_e_ignorada()
    {
        // Caruaru, agreste: fora da grade.
        var chuva = CellReadingAssigner.AtribuirLeiturasACelulas([new Leitura(-8.28m, -35.97m, "CARUARU", 9m, null)], Grid);

        Assert.Empty(chuva);
    }

    [Fact]
    public void Mare_bate_com_a_tabua_nos_extremos_e_interpola_entre_eles()
    {
        // Extremos publicados pela DHN: 01/01 01:59 → 2,16 m; 08:12 → 0,47 m.
        var preamar = new DateTimeOffset(2026, 1, 1, 1, 59, 0, TimeSpan.FromHours(-3));
        var baixamar = new DateTimeOffset(2026, 1, 1, 8, 12, 0, TimeSpan.FromHours(-3));
        var meio = preamar + (baixamar - preamar) / 2;

        Assert.Equal(2.16m, TideCalculator.AlturaPrevistaM(preamar));
        Assert.InRange(TideCalculator.AlturaPrevistaM(meio), 1.30m, 1.33m); // média dos dois
        Assert.True(TideCalculator.FatorMare(preamar) > 1.5m);
        Assert.Equal(1m, TideCalculator.FatorMare(baixamar));
    }

    [Fact]
    public void Mare_fora_de_2026_falha_explicitamente()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TideCalculator.AlturaPrevistaM(new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Tempestade_simulada_cobre_a_grade_e_e_mais_forte_no_centro()
    {
        var pico = Array.IndexOf(RainSimulator.PerfilTempestadeMm, RainSimulator.PerfilTempestadeMm.Max());
        var chuva = RainSimulator.Gerar(Grid, pico);

        Assert.Equal(Grid.Count, chuva.Count);
        var bordaSul = Grid.Keys.First(k => Grid[k].Municipio == "Ipojuca");
        Assert.True(chuva[CelulaRecife] > 15m);
        Assert.True(chuva[CelulaRecife] > chuva[bordaSul]);
        Assert.Equal(chuva, RainSimulator.Gerar(Grid, pico)); // determinística
    }
}
