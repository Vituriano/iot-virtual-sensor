// Sensor virtual de alagamento — RMR.
//
// Dispositivo IoT virtual: na prática são vários sensores virtuais, um por
// célula H3 sobre os 14 municípios da RMR, cada um lendo a chuva mais
// próxima da sua célula (em vez de um pino físico) e reportando o valor
// bruto. O ciclo de vida tem 2 camadas explícitas: sensoriamento -> saída
// -> sono.
//
// O dashboard imprime a chuva máxima lida por município, agregando os
// sensores (células) que caem nele.
//
// Escopo atual: só a leitura de chuva mais recente de cada estação,
// associada à célula que a contém. 

using VirtualSensor.Grid;
using VirtualSensor.Sensing;

var cicloIntervalo = TimeSpan.FromMinutes(15); // alinhado à cadência do Cemaden/documento
var once = args.Contains("--once");

var grid = H3GridBuilder.BuildGrid();
Console.WriteLine($"Grade H3 carregada: {grid.Count} células (res {H3GridBuilder.H3Resolution}) cobrindo os 14 municípios da RMR.");

var cliente = new CemadenClient();

while (true)
{
    try
    {
        // --- Camada de sensoriamento: busca a chuva e associa à célula/sensor correspondente ---
        var leituras = await cliente.LeiturasRmrAsync();
        var chuvaPorCelula = CellReadingAssigner.AtribuirLeiturasACelulas(leituras, grid);

        // --- Saída ---
        ImprimirDashboard(grid, leituras, chuvaPorCelula);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        // Falha de rede ao falar com o feed da APAC/Cemaden: transitório,
        // o próximo ciclo tenta de novo sozinho.
        Console.WriteLine($"[falha de rede no ciclo, tentando de novo no próximo ciclo] {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[erro inesperado no ciclo, encerrando o sensor] {ex}");
        throw;
    }

    if (once)
    {
        break;
    }

    await Task.Delay(cicloIntervalo);
}

static void ImprimirDashboard(
    Dictionary<string, CellInfo> grid,
    List<Leitura> leituras,
    Dictionary<string, decimal> chuvaPorCelula)
{
    var totalCelulasMunicipio = new Dictionary<string, int>();
    foreach (var info in grid.Values)
    {
        totalCelulasMunicipio[info.Municipio] = totalCelulasMunicipio.GetValueOrDefault(info.Municipio) + 1;
    }

    var agregadoPorMunicipio = new Dictionary<string, Agregado>();
    foreach (var (cell, chuvaMm) in chuvaPorCelula)
    {
        var municipio = grid[cell].Municipio;
        var atual = agregadoPorMunicipio.GetValueOrDefault(municipio, new Agregado(0, 0m));
        agregadoPorMunicipio[municipio] = new Agregado(
            atual.CelulasComLeitura + 1,
            Math.Max(atual.ChuvaMaxMm, chuvaMm));
    }

    Console.WriteLine("--- SENSOR VIRTUAL DE ALAGAMENTO — RMR ---");
    Console.WriteLine($"Estações lidas: {leituras.Count} | Células com leitura: {chuvaPorCelula.Count} / {grid.Count}");
    Console.WriteLine($"{"Município",-28} {"Chuva máx (mm)",16} {"Cobertura",12}");

    foreach (var municipio in totalCelulasMunicipio.Keys.OrderBy(m => m))
    {
        var agregado = agregadoPorMunicipio.GetValueOrDefault(municipio, new Agregado(0, 0m));
        var cobertura = $"{agregado.CelulasComLeitura}/{totalCelulasMunicipio[municipio]}";
        Console.WriteLine($"{municipio,-28} {agregado.ChuvaMaxMm,16:F1} {cobertura,12}");
    }

    Console.WriteLine(new string('-', 60));
}

file readonly record struct Agregado(int CelulasComLeitura, decimal ChuvaMaxMm);
