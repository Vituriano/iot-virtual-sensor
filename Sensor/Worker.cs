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
/// A cada ciclo, o Worker publica a leitura em <see cref="LeituraStore"/>
/// pra Platform consumir. É a única ligação entre os dois processos.
/// </summary>
public class Worker(ILogger<Worker> logger, IHostApplicationLifetime lifetime) : BackgroundService
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var leituras = await cliente.LeiturasRmrAsync(stoppingToken);
                var chuvaPorCelula = CellReadingAssigner.AtribuirLeiturasACelulas(leituras, grid);

                var agora = DateTimeOffset.UtcNow;
                LeituraStore.Salvar(new SnapshotLeituras(agora, chuvaPorCelula));

                logger.LogInformation(
                    "Ciclo publicado: {Estacoes} estações lidas, {Celulas}/{Total} células com leitura.",
                    leituras.Count, chuvaPorCelula.Count, grid.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Falha de rede ao falar com o feed da APAC/Cemaden: transitório,
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
}
