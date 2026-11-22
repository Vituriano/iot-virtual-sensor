using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using VirtualSensor.Grid;
using VirtualSensor.Shared;

namespace VirtualSensor.Tests.Integracao;

/// <summary>
/// Fluxo real Sensor → broker MQTT → Platform → API HTTP (a mesma que o
/// Dashboard consome), tudo em processo: um broker MQTT de verdade
/// (MQTTnet.Server) numa porta livre e a Platform inteira de pé via
/// WebApplicationFactory. O "sensor" é um cliente MQTT publicando o mesmo
/// payload que o Worker publica.
/// </summary>
public sealed class FluxoPontaAPontaTests : IAsyncLifetime
{
    private static readonly string CelulaRecife = H3GridBuilder.LatLngToCellKey(-8.063m, -34.871m);
    private static readonly string CelulaOlinda = H3GridBuilder.LatLngToCellKey(-8.01m, -34.855m);

    private readonly int _porta = PortaLivre();
    private readonly string _historico = Path.Combine(Path.GetTempPath(), $"historico-{Guid.NewGuid():N}.jsonl");
    private MqttServer _broker = null!;

    public async Task InitializeAsync()
    {
        var fabrica = new MqttServerFactory();
        _broker = fabrica.CreateMqttServer(
            fabrica.CreateServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(_porta).Build());
        await _broker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _broker.StopAsync();
        _broker.Dispose();
        File.Delete(_historico);
    }

    [Fact]
    public async Task Sem_leitura_a_API_responde_503()
    {
        await using var platform = SubirPlatform();
        var http = platform.CreateClient();

        var resposta = await http.GetAsync("/risco/atual");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
    }

    [Fact]
    public async Task Leitura_publicada_pelo_sensor_aparece_na_API_como_GeoJSON()
    {
        await using var platform = SubirPlatform();
        var http = platform.CreateClient();
        await EsperarPlatformAssinando();

        var ts = DateTimeOffset.UtcNow;
        await Publicar(new LeituraCelula(CelulaRecife, ts, 25m));

        var celula = await EsperarCelula(http, CelulaRecife, ts);
        Assert.Equal("Recife", celula.GetProperty("municipio").GetString());
        Assert.Equal("alta", celula.GetProperty("confianca").GetString());
        Assert.Equal(25m, celula.GetProperty("chuva_1h_mm").GetDecimal());
        Assert.InRange(celula.GetProperty("processado_em").GetDateTimeOffset(), ts, DateTimeOffset.UtcNow);
        Assert.True(celula.GetProperty("nivel").GetInt32() >= 1, "25 mm em 15 min deveria gerar algum risco");
    }

    [Fact]
    public async Task Sensores_sao_independentes_e_leitura_antiga_perde_confianca()
    {
        await using var platform = SubirPlatform();
        var http = platform.CreateClient();
        await EsperarPlatformAssinando();

        var agora = DateTimeOffset.UtcNow;
        var antiga = agora.AddHours(-2);
        await Publicar(new LeituraCelula(CelulaRecife, agora, 0m));
        await Publicar(new LeituraCelula(CelulaOlinda, antiga, 0m));

        Assert.Equal("alta", (await EsperarCelula(http, CelulaRecife, agora)).GetProperty("confianca").GetString());
        Assert.Equal("sem dado recente", (await EsperarCelula(http, CelulaOlinda, antiga)).GetProperty("confianca").GetString());
    }

    [Fact]
    public async Task Mensagem_invalida_nao_derruba_a_ingestao()
    {
        await using var platform = SubirPlatform();
        var http = platform.CreateClient();
        await EsperarPlatformAssinando();

        await PublicarBruto(LeituraMqtt.TopicoDe(CelulaRecife), "isto não é json"u8.ToArray());
        await PublicarBruto(LeituraMqtt.TopicoDe("celula-que-nao-existe"), LeituraMqtt.Serializar(new LeituraCelula("x", DateTimeOffset.UtcNow, 1m)));
        var ts = DateTimeOffset.UtcNow;
        await Publicar(new LeituraCelula(CelulaRecife, ts, 2m));

        await EsperarCelula(http, CelulaRecife, ts);
    }

    [Fact]
    public async Task Platform_reiniciada_recupera_o_estado_pelo_retain()
    {
        var ts = DateTimeOffset.UtcNow;
        await Publicar(new LeituraCelula(CelulaRecife, ts, 4m)); // publicado antes de a Platform existir

        await using var platform = SubirPlatform();
        var http = platform.CreateClient();

        await EsperarCelula(http, CelulaRecife, ts);
    }

    // ---------------------------------------------------------------- apoio

    private WebApplicationFactory<Program> SubirPlatform() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Mqtt:Host", "127.0.0.1")
            .UseSetting("Mqtt:Port", _porta.ToString())
            .UseSetting("Estado:HistoricoPath", _historico));

    private async Task EsperarPlatformAssinando()
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limite)
        {
            if ((await _broker.GetClientsAsync()).Any(c => c.Id == "virtual-sensor-platform"))
            {
                await Task.Delay(200); // conexão aceita; dá tempo do SUBSCRIBE chegar
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Platform não conectou no broker.");
    }

    private Task Publicar(LeituraCelula leitura) =>
        PublicarBruto(LeituraMqtt.TopicoDe(leitura.H3), LeituraMqtt.Serializar(leitura));

    private async Task PublicarBruto(string topico, byte[] payload)
    {
        using var cliente = new MqttClientFactory().CreateMqttClient();
        await cliente.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _porta).WithClientId($"teste-{Guid.NewGuid():N}").Build());
        await cliente.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topico)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build());
        await cliente.DisconnectAsync();
    }

    /// <summary>Consulta a API (como o Dashboard faz) até a célula aparecer com a leitura esperada.</summary>
    private static async Task<JsonElement> EsperarCelula(HttpClient http, string h3, DateTimeOffset ts)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limite)
        {
            var resposta = await http.GetAsync("/risco/atual");
            if (resposta.IsSuccessStatusCode)
            {
                Assert.Equal("application/geo+json", resposta.Content.Headers.ContentType?.MediaType);
                var geo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("FeatureCollection", geo.GetProperty("type").GetString());
                foreach (var f in geo.GetProperty("features").EnumerateArray())
                {
                    var p = f.GetProperty("properties");
                    if (p.GetProperty("h3").GetString() == h3 && p.GetProperty("ultima_leitura").GetDateTimeOffset() == ts)
                    {
                        Assert.Equal("Polygon", f.GetProperty("geometry").GetProperty("type").GetString());
                        return p;
                    }
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Célula {h3} não apareceu na API.");
    }

    private static int PortaLivre()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var porta = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return porta;
    }
}
