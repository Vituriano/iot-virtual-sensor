using VirtualSensor.Shared;

namespace VirtualSensor.Tests.Unit;

/// <summary>Contrato MQTT entre Sensor e Platform (tópico + payload).</summary>
public class ContratoMqttTests
{
    [Fact]
    public void Topico_por_celula_ida_e_volta()
    {
        var topico = LeituraMqtt.TopicoDe("88819b0a6dfffff");

        Assert.Equal("rmr/chuva/88819b0a6dfffff", topico);
        Assert.Equal("88819b0a6dfffff", LeituraMqtt.H3De(topico));
    }

    [Theory]
    [InlineData("rmr/mare/88819b0a6dfffff")]
    [InlineData("outro/topico")]
    public void Topico_fora_do_prefixo_e_ignorado(string topico)
    {
        Assert.Null(LeituraMqtt.H3De(topico));
    }

    [Fact]
    public void Payload_serializa_e_desserializa_sem_perda()
    {
        var original = new LeituraCelula("88819b0a6dfffff", new DateTimeOffset(2026, 11, 22, 10, 30, 0, TimeSpan.Zero), 12.4m);

        var payload = LeituraMqtt.Serializar(original);
        var lida = LeituraMqtt.Desserializar(original.H3, payload);

        Assert.Equal(original, lida);
    }

    [Fact]
    public void Payload_tem_formato_json_documentado()
    {
        var payload = LeituraMqtt.Serializar(new LeituraCelula("x", new DateTimeOffset(2026, 11, 22, 10, 30, 0, TimeSpan.Zero), 3.5m));

        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var raiz = json.RootElement;
        Assert.Equal(["ts", "chuva_mm"], raiz.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-11-22T10:30:00.0000000+00:00", raiz.GetProperty("ts").GetString()); // ISO 8601
        Assert.Equal(3.5m, raiz.GetProperty("chuva_mm").GetDecimal());
    }
}
