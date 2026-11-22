using System.Buffers;
using MQTTnet;
using MQTTnet.Protocol;
using VirtualSensor.Grid;
using VirtualSensor.Processing;
using VirtualSensor.Shared;

namespace VirtualSensor.Platform;

/// <summary>
/// Assina os tópicos MQTT dos sensores (<see cref="LeituraMqtt.FiltroTodos"/>)
/// e, a cada leitura de uma célula, cruza com maré e terreno, calcula o
/// risco só daquela célula e atualiza a entrada dela no
/// <see cref="RiscoCache"/> que o endpoint HTTP lê. Não há ciclo: cada
/// sensor é independente. Essa é a Plataforma
/// IoT: sensor e plataforma são processos separados, ligados só pelo broker.
/// </summary>
public sealed class RiscoIngestorService(
    RiscoCache cache, RainHistoryStore historico, IConfiguration config, ILogger<RiscoIngestorService> logger) : BackgroundService
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
                    var h3 = LeituraMqtt.H3De(e.ApplicationMessage.Topic);
                    if (h3 is null || !grid.TryGetValue(h3, out var info))
                    {
                        return Task.CompletedTask;
                    }

                    var leitura = LeituraMqtt.Desserializar(h3, e.ApplicationMessage.Payload.ToArray());
                    var acumulado = historico.Registrar(h3, leitura.Ts, leitura.ChuvaMm);
                    var fatorM = TideCalculator.FatorMare(acumulado.UltimaLeituraTs);
                    var risco = RiskCalculator.CalcularRisco(
                        info, acumulado, fatorM, suscetibilidadePorCelula.GetValueOrDefault(h3));

                    cache.Atualizar(h3, risco);
                    logger.LogDebug("Risco de {H3} atualizado: nível {Nivel}.", h3, risco.Nivel);
                }
                catch (Exception ex)
                {
                    // Diferente do Sensor (que derruba o processo num erro
                    // inesperado): aqui tem gente consumindo a API ao vivo, então
                    // uma leitura com erro (ex.: tábua de maré esgotada no fim do
                    // ano) fica logado como erro alto, mas a API continua no ar
                    // servindo o último risco válido em vez de cair inteira.
                    logger.LogError(ex, "Falha ao processar uma leitura do sensor.");
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
                        .WithTopicFilter(f => f.WithTopic(LeituraMqtt.FiltroTodos).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build(),
                    stoppingToken);
                logger.LogInformation("Assinando {Topico} em {Host}:{Port}.", LeituraMqtt.FiltroTodos, host, port);

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
