// Testes experimentais contra o sistema rodando de verdade (broker
// Mosquitto + Platform). Faz o papel do Sensor publicando no broker e do
// Dashboard consultando a API, e mede o que acontece entre os dois.
//
//   dotnet run --project Experimentos                 # todos
//   dotnet run --project Experimentos -- latencia     # só um
//
// Publica sem retain: não deixa leitura sintética "presa" no broker.
// Saída em Markdown, pronta pra colar em docs/testes.md.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using VirtualSensor.Grid;
using VirtualSensor.Shared;

var brokerHost = Environment.GetEnvironmentVariable("MQTT_HOST") ?? "localhost";
var platformUrl = Environment.GetEnvironmentVariable("PLATFORM_URL") ?? "http://localhost:5128";
// "carga" por último: ela publica em todas as células, e E3/E4 precisam de
// células que ainda não receberam leitura nesta Platform.
var selecionados = args.Length > 0 ? args : ["latencia", "escalonamento", "confianca", "carga"];

var grid = H3GridBuilder.BuildGrid();
var http = new HttpClient { BaseAddress = new Uri(platformUrl), Timeout = TimeSpan.FromSeconds(30) };
using var mqtt = new MqttClientFactory().CreateMqttClient();
await mqtt.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(brokerHost, 1883).WithClientId("experimentos").Build());

// Células que a Platform ainda não conhece (sem histórico): E3 e E4 usam
// só essas, pra que leituras de rodadas anteriores não entrem na conta.
var jaVistas = new HashSet<string>();
var inicial = await http.GetAsync("/risco/atual");
if (inicial.IsSuccessStatusCode)
{
    foreach (var c in Celulas(await inicial.Content.ReadFromJsonAsync<JsonElement>())) jaVistas.Add(c.H3);
}
string[] Livres(string municipio, int n)
{
    var livres = grid.Keys.Where(k => grid[k].Municipio == municipio && !jaVistas.Contains(k)).Take(n).ToArray();
    if (livres.Length < n)
        throw new InvalidOperationException("Sem células novas: reinicie a Platform com histórico limpo (ver docs/testes.md).");
    foreach (var k in livres) jaVistas.Add(k);
    return livres;
}

Console.WriteLine($"# Resultados dos experimentos — {DateTimeOffset.Now:yyyy-MM-dd HH:mm} (UTC{DateTimeOffset.Now:zzz})");
Console.WriteLine();
Console.WriteLine($"Broker `{brokerHost}:1883` · Platform `{platformUrl}` · grade com {grid.Count} células · {Environment.ProcessorCount} núcleos · .NET {Environment.Version}");

foreach (var nome in selecionados)
{
    switch (nome)
    {
        case "latencia": await Latencia(); break;
        case "carga": await Carga(); break;
        case "escalonamento": await Escalonamento(); break;
        case "confianca": await Confianca(); break;
        default: Console.Error.WriteLine($"Experimento desconhecido: {nome}"); return 1;
    }
}

await mqtt.DisconnectAsync();
return 0;

// E1 — quanto tempo uma leitura leva do publish do sensor até estar
// visível na API que o Dashboard consome.
async Task Latencia()
{
    const int rodadas = 30;
    var celula = H3GridBuilder.LatLngToCellKey(-8.063m, -34.871m); // Marco Zero
    var amostras = new List<double>();

    for (var i = 0; i < rodadas; i++)
    {
        var ts = DateTimeOffset.UtcNow;
        var relogio = Stopwatch.StartNew();
        await Publicar(new LeituraCelula(celula, ts, 1m));
        await EsperarAte(geo => Celulas(geo).Any(c => c.H3 == celula && c.Ts == ts), TimeSpan.FromMilliseconds(5));
        amostras.Add(relogio.Elapsed.TotalMilliseconds);
        await Task.Delay(100);
    }

    Console.WriteLine();
    Console.WriteLine("## E1 · Latência ponta a ponta (publish MQTT → visível em `GET /risco/atual`)");
    Console.WriteLine();
    Console.WriteLine($"{rodadas} leituras na célula `{celula}` (Recife), uma por vez.");
    Console.WriteLine();
    Console.WriteLine("| mín | mediana | p95 | máx | média |");
    Console.WriteLine("|---|---|---|---|---|");
    Console.WriteLine($"| {Ms(amostras.Min())} | {Ms(Percentil(amostras, 50))} | {Ms(Percentil(amostras, 95))} | {Ms(amostras.Max())} | {Ms(amostras.Average())} |");
}

// E2 — todas as 3.210 células publicando no mesmo ciclo (o pior caso do
// Sensor): vazão do broker/Platform e custo da resposta da API cheia.
async Task Carga()
{
    var ts = DateTimeOffset.UtcNow;
    var relogio = Stopwatch.StartNew();
    foreach (var h3 in grid.Keys)
    {
        await Publicar(new LeituraCelula(h3, ts, 0.5m));
    }
    var tPublicar = relogio.Elapsed;

    await EsperarAte(geo => Celulas(geo).Count(c => c.Ts == ts) == grid.Count, TimeSpan.FromMilliseconds(20));
    var tTotal = relogio.Elapsed;

    var consultas = new List<double>();
    long bytes = 0;
    for (var i = 0; i < 10; i++)
    {
        var r = Stopwatch.StartNew();
        var corpo = await http.GetByteArrayAsync("/risco/atual");
        consultas.Add(r.Elapsed.TotalMilliseconds);
        bytes = corpo.Length;
    }

    Console.WriteLine();
    Console.WriteLine("## E2 · Carga: um ciclo com as 3.210 células publicando");
    Console.WriteLine();
    Console.WriteLine("| métrica | valor |");
    Console.WriteLine("|---|---|");
    Console.WriteLine($"| mensagens publicadas (QoS 1) | {grid.Count} |");
    Console.WriteLine($"| tempo para publicar todas | {Ms(tPublicar.TotalMilliseconds)} |");
    Console.WriteLine($"| tempo até todas visíveis na API | {Ms(tTotal.TotalMilliseconds)} |");
    Console.WriteLine($"| vazão ponta a ponta | {grid.Count / tTotal.TotalSeconds:F0} leituras/s |");
    Console.WriteLine($"| tamanho da resposta `GET /risco/atual` | {bytes / 1024.0 / 1024.0:F2} MB |");
    Console.WriteLine($"| tempo de resposta da API (mediana de 10) | {Ms(Percentil(consultas, 50))} |");
}

// E3 — uma célula recebendo uma tempestade em passos de 15 min: o risco
// precisa subir com a chuva acumulada e cair quando ela sai da janela.
async Task Escalonamento()
{
    var celula = Livres("Recife", 1)[0];
    decimal[] perfil = [0m, 2m, 6m, 12m, 20m, 14m, 6m, 2m, 0m, 0m, 0m, 0m];
    var inicio = DateTimeOffset.UtcNow.AddMinutes(-15 * perfil.Length).AddSeconds(-DateTimeOffset.UtcNow.Second);

    Console.WriteLine();
    Console.WriteLine("## E3 · Resposta do modelo a uma tempestade (uma célula, passos de 15 min)");
    Console.WriteLine();
    Console.WriteLine($"Célula `{celula}` (Recife). Cada linha é uma leitura publicada; os valores são os que a API devolveu.");
    Console.WriteLine();
    Console.WriteLine("| passo | chuva (mm) | acum. 1 h | P | M (maré) | S (terreno) | nível | prob. |");
    Console.WriteLine("|---|---|---|---|---|---|---|---|");

    for (var i = 0; i < perfil.Length; i++)
    {
        var ts = inicio.AddMinutes(15 * i);
        await Publicar(new LeituraCelula(celula, ts, perfil[i]));
        var geo = await EsperarAte(g => Celulas(g).Any(c => c.H3 == celula && c.Ts == ts), TimeSpan.FromMilliseconds(10));
        var p = Celulas(geo).First(c => c.H3 == celula).Props;
        var f = p.GetProperty("fatores");
        Console.WriteLine($"| {i} | {perfil[i]} | {p.GetProperty("chuva_1h_mm").GetDecimal():0.#} | {f.GetProperty("p").GetDecimal():F2} | {f.GetProperty("m").GetDecimal():F2} | {f.GetProperty("s").GetDecimal():F2} | {p.GetProperty("nivel").GetInt32()} | {p.GetProperty("probabilidade").GetDecimal() * 100:F0}% |");
    }
}

// E4 — a confiança é calculada na consulta, pela idade da leitura.
async Task Confianca()
{
    var celulas = Livres("Olinda", 4);
    int[] idadesMin = [0, 15, 45, 120];

    Console.WriteLine();
    Console.WriteLine("## E4 · Confiança pela idade da última leitura");
    Console.WriteLine();
    Console.WriteLine("| idade da leitura | confiança devolvida |");
    Console.WriteLine("|---|---|");
    for (var i = 0; i < celulas.Length; i++)
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-idadesMin[i]);
        await Publicar(new LeituraCelula(celulas[i], ts, 0m));
        var geo = await EsperarAte(g => Celulas(g).Any(c => c.H3 == celulas[i] && c.Ts == ts), TimeSpan.FromMilliseconds(10));
        var conf = Celulas(geo).First(c => c.H3 == celulas[i]).Props.GetProperty("confianca").GetString();
        Console.WriteLine($"| {idadesMin[i]} min | {conf} |");
    }
}

// ---------------------------------------------------------------- apoio

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
        await Task.Delay(intervalo);
    }
    throw new TimeoutException("A Platform não refletiu a leitura em 60 s. Ela está rodando?");
}

static IEnumerable<(string H3, DateTimeOffset Ts, JsonElement Props)> Celulas(JsonElement geo) =>
    geo.GetProperty("features").EnumerateArray().Select(f =>
    {
        var p = f.GetProperty("properties");
        return (p.GetProperty("h3").GetString()!, p.GetProperty("ultima_leitura").GetDateTimeOffset(), p);
    });

static double Percentil(List<double> xs, double p)
{
    var ordenado = xs.Order().ToArray();
    var idx = (int)Math.Ceiling(p / 100 * ordenado.Length) - 1;
    return ordenado[Math.Clamp(idx, 0, ordenado.Length - 1)];
}

static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:F2} s" : $"{ms:F0} ms";
