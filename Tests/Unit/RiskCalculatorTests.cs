using VirtualSensor.Grid;
using VirtualSensor.Processing;

namespace VirtualSensor.Tests.Unit;

/// <summary>Regra de negócio do risco: R = P · M · S, níveis, probabilidade e confiança.</summary>
public class RiskCalculatorTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.29, 0)]
    [InlineData(0.30, 1)]
    [InlineData(0.69, 1)]
    [InlineData(0.70, 2)]
    [InlineData(1.19, 2)]
    [InlineData(1.20, 3)]
    [InlineData(5.00, 3)]
    public void Nivel_respeita_os_cortes(decimal r, int nivelEsperado)
    {
        Assert.Equal(nivelEsperado, RiskCalculator.NivelDeRisco(r));
    }

    [Fact]
    public void Probabilidade_fica_entre_0_e_1_e_nunca_diminui_com_R()
    {
        var anterior = -1m;
        for (var r = 0m; r <= 2m; r += 0.05m)
        {
            var prob = RiskCalculator.CalcularProbabilidade(r);
            Assert.InRange(prob, 0m, 1m);
            Assert.True(prob >= anterior, $"probabilidade caiu em R={r}");
            anterior = prob;
        }
    }

    [Theory]
    [InlineData(0, "alta")]
    [InlineData(20, "alta")]
    [InlineData(21, "média")]
    [InlineData(60, "média")]
    [InlineData(61, RiskCalculator.SemDadoRecente)]
    public void Confianca_cai_com_a_idade_da_leitura(int minutos, string esperado)
    {
        Assert.Equal(esperado, RiskCalculator.CalcularConfianca(Agora, Agora.AddMinutes(-minutos)));
    }

    [Fact]
    public void Fator_de_chuva_usa_a_janela_mais_critica()
    {
        // 15 min: 5/10 = 0.5 · 1 h: 24/30 = 0.8 · 3 h: 30/60 = 0.5 → P = 0.8
        var acumulado = new AcumuladoCelula(5m, 24m, 30m, Agora);

        Assert.Equal(0.8m, RiskCalculator.FatorChuva(acumulado));
    }

    [Fact]
    public void Risco_e_o_produto_dos_tres_fatores()
    {
        var acumulado = new AcumuladoCelula(10m, 10m, 10m, Agora); // P = 1.0
        var risco = RiskCalculator.CalcularRisco(new CellInfo("Recife", 2611606), acumulado, fatorMare: 1.5m, suscetibilidade: 0.6m);

        Assert.Equal(1.0m, risco.P);
        Assert.Equal(0.9m, risco.R);
        Assert.Equal(2, risco.Nivel);
        Assert.Equal("Recife", risco.Municipio);
        Assert.Equal(Agora, risco.UltimaLeitura);
    }

    [Fact]
    public void Sem_chuva_nao_ha_risco_mesmo_com_mare_alta_e_terreno_ruim()
    {
        var risco = RiskCalculator.CalcularRisco(
            new CellInfo("Recife", 2611606), new AcumuladoCelula(0m, 0m, 0m, Agora), fatorMare: 2m, suscetibilidade: 1m);

        Assert.Equal(0, risco.Nivel);
        Assert.Equal(0m, risco.Probabilidade);
    }
}
