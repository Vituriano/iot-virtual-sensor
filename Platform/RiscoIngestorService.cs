using VirtualSensor.Grid;
using VirtualSensor.Processing;
using VirtualSensor.Shared;

namespace VirtualSensor.Platform;

/// <summary>
/// Fica de olho no arquivo que o Sensor publica (<see cref="LeituraStore"/>)
/// e, sempre que aparece um ciclo novo, cruza a chuva com maré e terreno,
/// calcula o risco por célula e atualiza o <see cref="RiscoCache"/> que o
/// endpoint HTTP lê. Essa é a Plataforma IoT: sensor e plataforma são
/// processos separados, ligados só por esse arquivo compartilhado.
/// </summary>
public sealed class RiscoIngestorService(RiscoCache cache, ILogger<RiscoIngestorService> logger) : BackgroundService
{
    private static readonly TimeSpan IntervaloChecagem = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grid = H3GridBuilder.BuildGrid();
        logger.LogInformation("Grade H3 carregada: {Count} células.", grid.Count);

        var suscetibilidadePorCelula = TerrainCalculator.CalcularSuscetibilidade();
        logger.LogInformation("Suscetibilidade de terreno calculada para {Count} células.", suscetibilidadePorCelula.Count);

        DateTimeOffset? ultimoCicloProcessado = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = LeituraStore.Carregar();
                if (snapshot is not null && snapshot.Ciclo != ultimoCicloProcessado)
                {
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

            await Task.Delay(IntervaloChecagem, stoppingToken);
        }
    }
}
