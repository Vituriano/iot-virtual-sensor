using VirtualSensor.Processing;

namespace VirtualSensor.Tests.Unit;

/// <summary>Histórico de chuva por célula: janelas 15 min / 1 h / 3 h, idempotência e persistência.</summary>
public sealed class RainHistoryStoreTests : IDisposable
{
    private readonly string _arquivo = Path.Combine(Path.GetTempPath(), $"historico-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(_arquivo);

    // Persistência descarta o que tem mais de 3h15 em relação ao relógio
    // real, então os testes usam horários perto de agora.
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddHours(-3);

    [Fact]
    public void Acumula_por_janela_de_tempo()
    {
        var store = new RainHistoryStore(_arquivo);

        store.Registrar("c", T0, 10m);                         // fora de 1 h, dentro de 3 h
        store.Registrar("c", T0.AddMinutes(150), 4m);          // dentro de 1 h
        var acc = store.Registrar("c", T0.AddMinutes(170), 3m); // dentro de 15 min

        Assert.Equal(3m, acc.Min15);
        Assert.Equal(7m, acc.Hora1);
        Assert.Equal(17m, acc.Hora3);
        Assert.Equal(T0.AddMinutes(170), acc.UltimaLeituraTs);
    }

    [Fact]
    public void Leitura_repetida_nao_e_somada_duas_vezes()
    {
        // O broker reentrega a última leitura (retain) quando a Platform reconecta.
        var store = new RainHistoryStore(_arquivo);

        store.Registrar("c", T0, 5m);
        var acc = store.Registrar("c", T0, 5m);

        Assert.Equal(5m, acc.Min15);
    }

    [Fact]
    public void Celulas_nao_se_misturam()
    {
        var store = new RainHistoryStore(_arquivo);

        store.Registrar("a", T0, 50m);
        var acc = store.Registrar("b", T0, 1m);

        Assert.Equal(1m, acc.Hora3);
    }

    [Fact]
    public void Historico_sobrevive_a_reinicio()
    {
        new RainHistoryStore(_arquivo).Registrar("c", T0.AddMinutes(100), 8m);

        var reiniciado = new RainHistoryStore(_arquivo);
        var acc = reiniciado.Registrar("c", T0.AddMinutes(110), 2m);

        Assert.Equal(10m, acc.Min15);
    }

    [Fact]
    public void Leitura_fora_de_ordem_entra_na_soma_mas_nao_volta_o_estado_no_tempo()
    {
        var store = new RainHistoryStore(_arquivo);

        store.Registrar("c", T0.AddMinutes(30), 6m);
        var acc = store.Registrar("c", T0.AddMinutes(20), 4m); // chegou atrasada

        Assert.Equal(T0.AddMinutes(30), acc.UltimaLeituraTs);
        Assert.Equal(10m, acc.Min15);
    }
}
