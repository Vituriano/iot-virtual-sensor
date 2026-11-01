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
/// A cada ciclo, o Worker publica a leitura via MQTT (Wi-Fi, TCP/1883) no
/// tópico <see cref="LeituraMqtt.TopicoCiclo"/> pra Platform consumir. É a
/// única ligação entre os dois processos.
/// </summary>
public class Worker(ILogger<Worker> logger, IHostApplicationLifetime lifetime, IConfiguration config) : BackgroundService
{
    private static readonly TimeSpan CicloIntervalo = TimeSpan.FromMinutes(15);

    // --once roda um ciclo só e encerra — útil pra testar localmente sem
    // esperar 15 minutos. Não afeta o comportamento normal do serviço.
    private static readonly bool Once = Environment.GetCommandLineArgs().Contains("--once");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grid = H3GridBuilder.BuildGrid();
        logger.LogInformation(
            "Grade H3 carregada: {Count} células (res {Res}) cobrindo os 14 municípios da RMR.",
            grid.Count, H3GridBuilder.H3Resolution);

        var cliente = new CemadenClient();
        var brokerHost = config["Mqtt:Host"] ?? "localhost";
        var brokerPort = int.Parse(config["Mqtt:Port"] ?? "1883");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var leituras = await cliente.LeiturasRmrAsync(stoppingToken);
                var chuvaPorCelula = CellReadingAssigner.AtribuirLeiturasACelulas(leituras, grid);

                var agora = DateTimeOffset.UtcNow;
                await PublicarAsync(new SnapshotLeituras(agora, chuvaPorCelula), brokerHost, brokerPort, stoppingToken);

                logger.LogInformation(
                    "Ciclo publicado: {Estacoes} estações lidas, {Celulas}/{Total} células com leitura.",
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

            await Task.Delay(CicloIntervalo, stoppingToken);
        }
    }

    // Conecta, publica e desconecta a cada ciclo: com 15 min entre
    // publicações, manter a conexão aberta só adiciona reconexão pra tratar.
    private static async Task PublicarAsync(SnapshotLeituras snapshot, string host, int port, CancellationToken ct)
    {
        using var mqtt = new MqttClientFactory().CreateMqttClient();
        var opcoes = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("virtual-sensor-rmr")
            .Build();

        await mqtt.ConnectAsync(opcoes, ct);

        var mensagem = new MqttApplicationMessageBuilder()
            .WithTopic(LeituraMqtt.TopicoCiclo)
            .WithPayload(LeituraMqtt.Serializar(snapshot))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build();

        await mqtt.PublishAsync(mensagem, ct);
        await mqtt.DisconnectAsync(cancellationToken: ct);
    }
}
