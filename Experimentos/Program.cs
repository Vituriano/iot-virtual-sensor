// Testes experimentais contra o sistema rodando de verdade: broker
// Mosquitto + Platform. Faz o papel do Sensor (publica no broker) e do
// Dashboard (consulta a API) e mede o que acontece entre os dois.
//
// Rode por scripts/experimentos.sh, que sobe broker e Platform limpos.
// Direto, com broker e Platform limpos já no ar:
//
//   dotnet run --project Experimentos -- docs/resultados
//
// Publica sem retain (não deixa leitura sintética presa no broker). Grava
// os dados brutos em CSV e o resumo em Markdown na pasta de saída.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using VirtualSensor.Grid;
using VirtualSensor.Shared;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var brokerHost = Environment.GetEnvironmentVariable("MQTT_HOST") ?? "localhost";
var platformUrl = Environment.GetEnvironmentVariable("PLATFORM_URL") ?? "http://localhost:5128";
int? platformPid = int.TryParse(Environment.GetEnvironmentVariable("PLATFORM_PID"), out var pid) ? pid : null;
var saida = args.Length > 0 ? args[0] : Path.Combine("docs", "resultados");
Directory.CreateDirectory(saida);

// Varredura de carga: quantos sensores publicam no mesmo ciclo.
int[] tamanhos = [1, 10, 100, 500, 1000, 2000, 3210];
const int Repeticoes = 5;
const int CiclosEstabilidade = 20;

var grid = H3GridBuilder.BuildGrid();
var ordenadas = grid.Keys.Order(StringComparer.Ordinal).ToArray();
var http = new HttpClient { BaseAddress = new Uri(platformUrl), Timeout = TimeSpan.FromSeconds(30) };
using var mqtt = new MqttClientFactory().CreateMqttClient();
await mqtt.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(brokerHost, 1883).WithClientId("experimentos").Build());

// Leituras de antes (ex.: retidas no broker depois de rodar o Sensor)
// entrariam na conta do E3/E4 e no tamanho da API do E2.
if ((await http.GetAsync("/risco/atual")).IsSuccessStatusCode)
{
    Console.Error.WriteLine("A Platform já tem leituras. Rode por scripts/experimentos.sh, que sobe broker e Platform limpos.");
    return 1;
}

Progresso("E3 · modelo");
var e3 = await Modelo();
Progresso("E4 · confiança");
var e4 = await Confianca();
Progresso("E2 · escalabilidade");
var e2 = await Escalabilidade();
Progresso("E1 · latência com as 3.210 células ativas");
var e1 = await LatenciaCargaTotal();
Progresso("E6 · estabilidade");
var e6 = await Estabilidade();

var md = new StringBuilder()
    .AppendLine($"# Resultados dos experimentos — {DateTimeOffset.Now:yyyy-MM-dd HH:mm} (UTC{DateTimeOffset.Now:zzz})")
    .AppendLine()
    .AppendLine($"Broker `{brokerHost}:1883` · Platform `{platformUrl}` · grade com {grid.Count} células · " +
                $"{Environment.ProcessorCount} núcleos · .NET {Environment.Version} · {RuntimeInformation.OSDescription}")
    .AppendLine()
    .AppendLine("Dados brutos nos `.csv` desta pasta; gráficos em `graficos/`.")
    .Append(e1).Append(e2).Append(e3).Append(e4).Append(e6)
    .ToString();
await File.WriteAllTextAsync(Path.Combine(saida, "experimentos.md"), md);
Console.WriteLine(md);

await mqtt.DisconnectAsync();
return 0;

// E1 — uma leitura por vez, com a Platform já servindo as 3.210 células
// (a situação normal de operação). Duas medidas: dentro do pipeline
// (publish → risco calculado) e o que um cliente consultando a API vê.
async Task<string> LatenciaCargaTotal()
{
    const int rodadas = 30;
    var celula = H3GridBuilder.LatLngToCellKey(-8.063m, -34.871m); // Marco Zero
    var csv = new StringBuilder("amostra,ingestao_ms,visivel_api_ms\n");
    var ingestao = new List<double>();
    var visivel = new List<double>();

    for (var i = 1; i <= rodadas; i++)
    {
        var ts = DateTimeOffset.UtcNow;
        var relogio = Stopwatch.StartNew();
        await Publicar(new LeituraCelula(celula, ts, 0m));
        var geo = await EsperarAte(g => Indexar(g).TryGetValue(celula, out var c) && c.Ts == ts, TimeSpan.Zero);
        var vis = relogio.Elapsed.TotalMilliseconds;
        var ing = (Indexar(geo)[celula].ProcessadoEm - ts).TotalMilliseconds;

        ingestao.Add(ing);
        visivel.Add(vis);
        csv.AppendLine($"{i},{ing:F3},{vis:F3}");
        await Task.Delay(200);
    }
    await File.WriteAllTextAsync(Path.Combine(saida, "latencia.csv"), csv.ToString());

    return new StringBuilder()
        .AppendLine().AppendLine("## E1 · Latência de uma leitura, com as 3.210 células ativas").AppendLine()
        .AppendLine($"{rodadas} leituras na célula `{celula}` (Recife), uma por vez. Arquivo: `latencia.csv`.").AppendLine()
        .AppendLine("| medida | p50 | p95 | máx |")
        .AppendLine("|---|---|---|---|")
        .AppendLine($"| publish → risco calculado na Platform | {Ms(Percentil(ingestao, 50))} | {Ms(Percentil(ingestao, 95))} | {Ms(ingestao.Max())} |")
        .AppendLine($"| publish → visível em `GET /risco/atual` | {Ms(Percentil(visivel, 50))} | {Ms(Percentil(visivel, 95))} | {Ms(visivel.Max())} |")
        .ToString();
}

// E2 — N sensores publicando no mesmo ciclo, N de 1 a 3.210, com
// repetições. Mesma forma de publicar do Sensor: em sequência, QoS 1.
async Task<string> Escalabilidade()
{
    var porMensagem = new StringBuilder("n_sensores,repeticao,h3,latencia_ms\n");
    var porExecucao = new StringBuilder(
        "n_sensores,repeticao,publicacao_ms,ciclo_pipeline_ms,visivel_api_ms,vazao_msgs_s,latencia_p50_ms,latencia_p95_ms,latencia_max_ms,api_resposta_ms,api_mb\n");
    var tabela = new StringBuilder()
        .AppendLine().AppendLine($"## E2 · Escalabilidade: N sensores publicando no mesmo ciclo ({Repeticoes} repetições)").AppendLine()
        .AppendLine("Latência por mensagem = publish → risco calculado (todas as mensagens das repetições juntas). " +
                    "Ciclo, vazão e API = mediana das repetições. Arquivos: `escalabilidade.csv` (por execução) e " +
                    "`escalabilidade_mensagens.csv` (por mensagem).").AppendLine()
        .AppendLine("| sensores | latência p50 | latência p95 | latência máx | ciclo no pipeline | visível na API | vazão | resposta da API | tamanho da API |")
        .AppendLine("|---|---|---|---|---|---|---|---|---|");

    foreach (var n in tamanhos)
    {
        var latencias = new List<double>();
        var ciclos = new List<double>();
        var visiveis = new List<double>();
        var vazoes = new List<double>();
        var apis = new List<double>();
        var mb = 0.0;

        for (var rep = 1; rep <= Repeticoes; rep++)
        {
            var r = await Ciclo(ordenadas.Take(n).ToArray());
            var (apiMs, apiMb) = await CustoApi();
            var lat = r.Latencias.Select(l => l.Ms).ToList();
            var vazao = n / (r.CicloPipelineMs / 1000.0);

            foreach (var (h3, ms) in r.Latencias) porMensagem.AppendLine($"{n},{rep},{h3},{ms:F3}");
            porExecucao.AppendLine(
                $"{n},{rep},{r.PublicacaoMs:F1},{r.CicloPipelineMs:F1},{r.VisivelMs:F1},{vazao:F0}," +
                $"{Percentil(lat, 50):F3},{Percentil(lat, 95):F3},{lat.Max():F3},{apiMs:F1},{apiMb:F3}");

            latencias.AddRange(lat);
            ciclos.Add(r.CicloPipelineMs);
            visiveis.Add(r.VisivelMs);
            vazoes.Add(vazao);
            apis.Add(apiMs);
            mb = apiMb;
            await Task.Delay(500);
        }

        tabela.AppendLine(
            $"| {n} | {Ms(Percentil(latencias, 50))} | {Ms(Percentil(latencias, 95))} | {Ms(latencias.Max())} | " +
            $"{Ms(Percentil(ciclos, 50))} | {Ms(Percentil(visiveis, 50))} | " +
            $"{(n >= 100 ? $"{Percentil(vazoes, 50):F0} msg/s" : "—")} | {Ms(Percentil(apis, 50))} | {Tamanho(mb)} |");
        Progresso($"   {n} sensores ok");
    }

    await File.WriteAllTextAsync(Path.Combine(saida, "escalabilidade_mensagens.csv"), porMensagem.ToString());
    await File.WriteAllTextAsync(Path.Combine(saida, "escalabilidade.csv"), porExecucao.ToString());
    return tabela.ToString();
}

// E3 — uma célula recebendo uma tempestade em passos de 15 min: o risco
// precisa subir com a chuva acumulada e cair quando ela sai da janela.
async Task<string> Modelo()
{
    var celula = H3GridBuilder.LatLngToCellKey(-8.05m, -34.90m); // Boa Vista, Recife
    decimal[] perfil = [0m, 2m, 6m, 12m, 20m, 14m, 6m, 2m, 0m, 0m, 0m, 0m];
    var inicio = DateTimeOffset.UtcNow.AddMinutes(-15 * perfil.Length);
    var csv = new StringBuilder("passo,ts,chuva_mm,acum_1h_mm,p,m,s,r,nivel,probabilidade\n");
    var tabela = new StringBuilder()
        .AppendLine().AppendLine("## E3 · Resposta do modelo a uma tempestade (uma célula, passos de 15 min)").AppendLine()
        .AppendLine($"Célula `{celula}` (Recife). Cada linha é uma leitura publicada; os valores são os que a API devolveu. Arquivo: `modelo.csv`.").AppendLine()
        .AppendLine("| passo | chuva (mm) | acum. 1 h | P | M (maré) | S (terreno) | R | nível | prob. |")
        .AppendLine("|---|---|---|---|---|---|---|---|---|");

    for (var i = 0; i < perfil.Length; i++)
    {
        var ts = inicio.AddMinutes(15 * i);
        await Publicar(new LeituraCelula(celula, ts, perfil[i]));
        var geo = await EsperarAte(g => Indexar(g).TryGetValue(celula, out var c) && c.Ts == ts, TimeSpan.FromMilliseconds(10));
        var p = Indexar(geo)[celula].Props;
        var f = p.GetProperty("fatores");
        var (fp, fm, fs) = (f.GetProperty("p").GetDecimal(), f.GetProperty("m").GetDecimal(), f.GetProperty("s").GetDecimal());
        var acum1h = p.GetProperty("chuva_1h_mm").GetDecimal();
        var nivel = p.GetProperty("nivel").GetInt32();
        var prob = p.GetProperty("probabilidade").GetDecimal();

        csv.AppendLine($"{i},{ts:o},{perfil[i]},{acum1h},{fp:F4},{fm:F4},{fs:F4},{fp * fm * fs:F4},{nivel},{prob:F4}");
        tabela.AppendLine($"| {i} | {perfil[i]} | {acum1h:0.#} | {fp:F2} | {fm:F2} | {fs:F2} | {fp * fm * fs:F2} | {nivel} | {prob * 100:F0}% |");
    }

    await File.WriteAllTextAsync(Path.Combine(saida, "modelo.csv"), csv.ToString());
    return tabela.ToString();
}

// E4 — a confiança é calculada na consulta, pela idade da leitura.
async Task<string> Confianca()
{
    var celulas = ordenadas.Where(k => grid[k].Municipio == "Olinda").Take(4).ToArray();
    int[] idadesMin = [0, 15, 45, 120];
    var tabela = new StringBuilder()
        .AppendLine().AppendLine("## E4 · Confiança pela idade da última leitura").AppendLine()
        .AppendLine("| idade da leitura | confiança devolvida |")
        .AppendLine("|---|---|");

    for (var i = 0; i < celulas.Length; i++)
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-idadesMin[i]);
        var h3 = celulas[i];
        await Publicar(new LeituraCelula(h3, ts, 0m));
        var geo = await EsperarAte(g => Indexar(g).TryGetValue(h3, out var c) && c.Ts == ts, TimeSpan.FromMilliseconds(10));
        tabela.AppendLine($"| {idadesMin[i]} min | {Indexar(geo)[h3].Props.GetProperty("confianca").GetString()} |");
    }
    return tabela.ToString();
}

// E6 — ciclos completos (3.210 sensores) em sequência, bem mais rápido
// que os 15 min reais: a latência e a memória da Platform ficam estáveis?
async Task<string> Estabilidade()
{
    var csv = new StringBuilder("ciclo,inicio_s,latencia_p50_ms,latencia_p95_ms,ciclo_pipeline_ms,visivel_api_ms,platform_rss_mb\n");
    var tabela = new StringBuilder()
        .AppendLine().AppendLine($"## E6 · Estabilidade: {CiclosEstabilidade} ciclos seguidos com as 3.210 células").AppendLine()
        .AppendLine("Um ciclo a cada ~2 s (o real é a cada 15 min). Arquivo: `estabilidade.csv`.").AppendLine()
        .AppendLine("| ciclo | latência p50 | latência p95 | ciclo no pipeline | visível na API | memória da Platform |")
        .AppendLine("|---|---|---|---|---|---|");
    var relogio = Stopwatch.StartNew();

    for (var c = 1; c <= CiclosEstabilidade; c++)
    {
        var inicio = relogio.Elapsed.TotalSeconds;
        var r = await Ciclo(ordenadas);
        var lat = r.Latencias.Select(l => l.Ms).ToList();
        var rss = RssMb();

        csv.AppendLine($"{c},{inicio:F1},{Percentil(lat, 50):F3},{Percentil(lat, 95):F3},{r.CicloPipelineMs:F1},{r.VisivelMs:F1},{(rss is { } m ? m.ToString("F1") : "")}");
        tabela.AppendLine($"| {c} | {Ms(Percentil(lat, 50))} | {Ms(Percentil(lat, 95))} | {Ms(r.CicloPipelineMs)} | {Ms(r.VisivelMs)} | {(rss is { } mb ? $"{mb:F0} MB" : "—")} |");
        await Task.Delay(2000);
    }

    await File.WriteAllTextAsync(Path.Combine(saida, "estabilidade.csv"), csv.ToString());
    return tabela.ToString();
}

// ---------------------------------------------------------------- apoio

// Publica uma leitura por célula, em sequência (como o Sensor), e espera
// todas aparecerem na API. Latência de cada mensagem: ts da leitura (pego
// logo antes do publish) até processado_em (quando a Platform calculou o
// risco). Mesmo relógio: tudo roda na mesma máquina.
async Task<ResultadoCiclo> Ciclo(string[] celulas)
{
    var ts = new Dictionary<string, DateTimeOffset>(celulas.Length);
    var relogio = Stopwatch.StartNew();
    foreach (var h3 in celulas)
    {
        var agora = DateTimeOffset.UtcNow;
        ts[h3] = agora;
        await Publicar(new LeituraCelula(h3, agora, 0m));
    }
    var publicacao = relogio.Elapsed.TotalMilliseconds;

    var geo = await EsperarAte(g =>
    {
        var vistas = Indexar(g);
        return celulas.All(h => vistas.TryGetValue(h, out var c) && c.Ts == ts[h]);
    }, TimeSpan.FromMilliseconds(10));
    var visivel = relogio.Elapsed.TotalMilliseconds;

    var idx = Indexar(geo);
    var latencias = celulas.Select(h => (h, (idx[h].ProcessadoEm - ts[h]).TotalMilliseconds)).ToList();
    var cicloPipeline = (celulas.Max(h => idx[h].ProcessadoEm) - ts.Values.Min()).TotalMilliseconds;
    return new ResultadoCiclo(latencias, publicacao, cicloPipeline, visivel);
}

async Task<(double Ms, double Mb)> CustoApi()
{
    var tempos = new List<double>();
    long bytes = 0;
    for (var i = 0; i < 10; i++)
    {
        var r = Stopwatch.StartNew();
        var corpo = await http.GetByteArrayAsync("/risco/atual");
        tempos.Add(r.Elapsed.TotalMilliseconds);
        bytes = corpo.Length;
    }
    return (Percentil(tempos, 50), bytes / 1024.0 / 1024.0);
}

Task Publicar(LeituraCelula l) => mqtt.PublishAsync(new MqttApplicationMessageBuilder()
    .WithTopic(LeituraMqtt.TopicoDe(l.H3))
    .WithPayload(LeituraMqtt.Serializar(l))
    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
    .Build());

async Task<JsonElement> EsperarAte(Func<JsonElement, bool> condicao, TimeSpan intervalo)
{
    var limite = Stopwatch.StartNew();
    while (limite.Elapsed < TimeSpan.FromSeconds(60))
    {
        var r = await http.GetAsync("/risco/atual");
        if (r.IsSuccessStatusCode)
        {
            var geo = await r.Content.ReadFromJsonAsync<JsonElement>();
            if (condicao(geo)) return geo;
        }
        if (intervalo > TimeSpan.Zero) await Task.Delay(intervalo);
    }
    throw new TimeoutException("A Platform não refletiu a leitura em 60 s. Ela está rodando?");
}

double? RssMb()
{
    var status = $"/proc/{platformPid}/status";
    if (platformPid is null || !File.Exists(status)) return null;
    var linha = File.ReadLines(status).FirstOrDefault(l => l.StartsWith("VmRSS:"));
    return linha is null ? null : double.Parse(linha.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024.0;
}

static Dictionary<string, (DateTimeOffset Ts, DateTimeOffset ProcessadoEm, JsonElement Props)> Indexar(JsonElement geo) =>
    geo.GetProperty("features").EnumerateArray()
        .Select(f => f.GetProperty("properties"))
        .ToDictionary(
            p => p.GetProperty("h3").GetString()!,
            p => (p.GetProperty("ultima_leitura").GetDateTimeOffset(), p.GetProperty("processado_em").GetDateTimeOffset(), p));

static double Percentil(List<double> xs, double p)
{
    var ordenado = xs.Order().ToArray();
    var idx = (int)Math.Ceiling(p / 100 * ordenado.Length) - 1;
    return ordenado[Math.Clamp(idx, 0, ordenado.Length - 1)];
}

static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:F2} s" : ms >= 10 ? $"{ms:F0} ms" : $"{ms:F1} ms";

static string Tamanho(double mb) => mb >= 1 ? $"{mb:F2} MB" : $"{mb * 1024:F0} KB";

static void Progresso(string etapa) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {etapa}");

record ResultadoCiclo(List<(string H3, double Ms)> Latencias, double PublicacaoMs, double CicloPipelineMs, double VisivelMs);
