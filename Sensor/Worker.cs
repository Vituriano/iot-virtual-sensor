using System.Net.Sockets;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using VirtualSensor.Grid;
using VirtualSensor.Sensing;
using VirtualSensor.Shared;

namespace VirtualSensor.Sensor;

/// <summary>
/// Dispositivo IoT virtual: na prática são vários sensores virtuais, um
/// por célula H3 sobre os 14 municípios da RMR, cada um lendo a chuva
/// mais próxima da sua célula (em vez de um pino físico) e reportando o
/// valor bruto. Nenhuma classificação de risco é feita aqui — isso é
/// regra de negócio da Platform, não sensoriamento.
///
/// A cada ciclo de leitura, cada sensor (célula) publica a sua chuva via
/// MQTT (Wi-Fi, TCP/1883) no próprio tópico <c>rmr/chuva/{h3}</c>. Os
/// sensores são independentes entre si: a Platform não espera "o ciclo
/// completo". É a única ligação entre os dois processos.
/// </summary>
public class Worker(ILogger<Worker> logger, IHostApplicationLifetime lifetime, IConfiguration config) : BackgroundService
{
    // --once roda um ciclo só e encerra — útil pra testar localmente sem
    // esperar 15 minutos. Não afeta o comportamento normal do serviço.
    private static readonly bool Once = Environment.GetCommandLineArgs().Contains("--once");

    // --simular troca o feed APAC/Cemaden por uma tempestade sintética
    // (RainSimulator) pra testar o fluxo inteiro sem depender de chover.
    // O resto (grade, MQTT, tópico, payload) é idêntico ao modo real.
    private static readonly bool Simular = Environment.GetCommandLineArgs().Contains("--simular");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grid = H3GridBuilder.BuildGrid();
        logger.LogInformation(
            "Grade H3 carregada: {Count} células (res {Res}) cobrindo os 14 municípios da RMR.",
            grid.Count, H3GridBuilder.H3Resolution);

        // 15 min por padrão (cadência do feed); configurável pra testes
        // (ex.: --Sensor:IntervaloSegundos=60 junto com --simular).
        var cicloIntervalo = TimeSpan.FromSeconds(int.Parse(config["Sensor:IntervaloSegundos"] ?? "900"));
        logger.LogInformation("Fonte: {Fonte}; ciclo a cada {Seg}s.",
            Simular ? "tempestade simulada" : "feed APAC/Cemaden", cicloIntervalo.TotalSeconds);

        var cliente = new CemadenClient();
        var ciclo = 0;
        var brokerHost = config["Mqtt:Host"] ?? "localhost";
        var brokerPort = int.Parse(config["Mqtt:Port"] ?? "1883");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<Leitura> leituras = Simular ? [] : await cliente.LeiturasRmrAsync(stoppingToken);
                var chuvaPorCelula = Simular
                    ? RainSimulator.Gerar(grid, ciclo++)
                    : CellReadingAssigner.AtribuirLeiturasACelulas(leituras, grid);

                var agora = DateTimeOffset.UtcNow;
                await PublicarAsync(agora, chuvaPorCelula, brokerHost, brokerPort, stoppingToken);

                logger.LogInformation(
                    "Leituras publicadas: {Estacoes} estações lidas, {Celulas}/{Total} sensores com leitura.",
                    leituras.Count, chuvaPorCelula.Count, grid.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or MqttCommunicationException or SocketException)
            {
                // Falha de rede ao falar com o feed da APAC/Cemaden ou com o
                // broker MQTT: transitório,
                // o próximo ciclo tenta de novo sozinho.
                logger.LogWarning("Falha de rede no ciclo, tentando de novo no próximo ciclo: {Erro}", ex.Message);
            }

            if (Once)
            {
                lifetime.StopApplication();
                return;
            }

            await Task.Delay(cicloIntervalo, stoppingToken);
        }
    }

    // Conecta, publica uma mensagem por sensor e desconecta a cada ciclo:
    // com 15 min entre leituras, manter a conexão aberta só adiciona
    // reconexão pra tratar.
    private static async Task PublicarAsync(
        DateTimeOffset ts, Dictionary<string, decimal> chuvaPorCelula, string host, int port, CancellationToken ct)
    {
        using var mqtt = new MqttClientFactory().CreateMqttClient();
        var opcoes = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("virtual-sensor-rmr")
            .Build();

        await mqtt.ConnectAsync(opcoes, ct);

        foreach (var (h3, chuvaMm) in chuvaPorCelula)
        {
            var mensagem = new MqttApplicationMessageBuilder()
                .WithTopic(LeituraMqtt.TopicoDe(h3))
                .WithPayload(LeituraMqtt.Serializar(new LeituraCelula(h3, ts, chuvaMm)))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag()
                .Build();

            await mqtt.PublishAsync(mensagem, ct);
        }

        await mqtt.DisconnectAsync(cancellationToken: ct);
    }
}
