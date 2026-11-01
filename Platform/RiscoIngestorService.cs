using System.Buffers;
using MQTTnet;
using MQTTnet.Protocol;
using VirtualSensor.Grid;
using VirtualSensor.Processing;
using VirtualSensor.Shared;

namespace VirtualSensor.Platform;

/// <summary>
/// Assina o tópico MQTT onde o Sensor publica a chuva de cada ciclo
/// (<see cref="LeituraMqtt.TopicoCiclo"/>) e, a cada mensagem, cruza com
/// maré e terreno, calcula o risco por célula e atualiza o
/// <see cref="RiscoCache"/> que o endpoint HTTP lê. Essa é a Plataforma
/// IoT: sensor e plataforma são processos separados, ligados só pelo broker.
/// </summary>
public sealed class RiscoIngestorService(
    RiscoCache cache, IConfiguration config, ILogger<RiscoIngestorService> logger) : BackgroundService
{
    private static readonly TimeSpan IntervaloReconexao = TimeSpan.FromSeconds(5);


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grid = H3GridBuilder.BuildGrid();
        logger.LogInformation("Grade H3 carregada: {Count} células.", grid.Count);

        var suscetibilidadePorCelula = TerrainCalculator.CalcularSuscetibilidade();
        logger.LogInformation("Suscetibilidade de terreno calculada para {Count} células.", suscetibilidadePorCelula.Count);

        var host = config["Mqtt:Host"] ?? "localhost";
        var port = int.Parse(config["Mqtt:Port"] ?? "1883");

        DateTimeOffset? ultimoCicloProcessado = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            using var mqtt = new MqttClientFactory().CreateMqttClient();
            var desconectou = new TaskCompletionSource();

            mqtt.DisconnectedAsync += _ =>
            {
                desconectou.TrySetResult();
                return Task.CompletedTask;
            };

            mqtt.ApplicationMessageReceivedAsync += e =>
            {
                try
                {
                    var snapshot = LeituraMqtt.Desserializar(e.ApplicationMessage.Payload.ToArray());
                    if (snapshot.Ciclo == ultimoCicloProcessado)
                    {
                        return Task.CompletedTask;
                    }

                    var registros = RainHistoryStore.RegistrarCiclo(snapshot.Ciclo, snapshot.ChuvaPorCelula);
                    var acumuladoPorCelula = RainHistoryStore.Acumulados(snapshot.Ciclo, registros);

                    var fatorM = TideCalculator.FatorMare(snapshot.Ciclo);

                    var riscoPorCelula = RiskCalculator.CalcularRisco(
                        grid, acumuladoPorCelula, fatorM, suscetibilidadePorCelula, snapshot.Ciclo);

                    cache.Atualizar(snapshot.Ciclo, riscoPorCelula);
                    ultimoCicloProcessado = snapshot.Ciclo;

                    logger.LogInformation(
                        "Risco recalculado para o ciclo {Ciclo}: {Count} células com leitura.",
                        snapshot.Ciclo, riscoPorCelula.Count);
                }
                catch (Exception ex)
                {
                    // Diferente do Sensor (que derruba o processo num erro
                    // inesperado): aqui tem gente consumindo a API ao vivo, então
                    // um ciclo com erro (ex.: tábua de maré esgotada no fim do
                    // ano) fica logado como erro alto, mas a API continua no ar
                    // servindo o último risco válido em vez de cair inteira.
                    logger.LogError(ex, "Falha ao processar o ciclo mais recente do sensor.");
                }

                return Task.CompletedTask;
            };

            try
            {
                await mqtt.ConnectAsync(
                    new MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId("virtual-sensor-platform").Build(),
                    stoppingToken);
                await mqtt.SubscribeAsync(
                    new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(LeituraMqtt.TopicoCiclo).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build(),
                    stoppingToken);
                logger.LogInformation("Assinando {Topico} em {Host}:{Port}.", LeituraMqtt.TopicoCiclo, host, port);

                await desconectou.Task.WaitAsync(stoppingToken);
                logger.LogWarning("Conexão com o broker MQTT perdida.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Broker MQTT indisponível ({Erro}); tentando de novo em {Seg}s.", ex.Message, IntervaloReconexao.TotalSeconds);
            }

            await Task.Delay(IntervaloReconexao, stoppingToken);
        }
    }
}
