// Plataforma IoT: recebe a chuva publicada pelo Sensor (via MQTT),
// cruza com maré e terreno (dado estático), calcula o risco por célula e
// expõe isso por HTTP. Roda como processo separado do Sensor — os dois só
// se falam através do broker MQTT.

using System.Collections.Concurrent;
using VirtualSensor.Grid;
using VirtualSensor.Platform;
using VirtualSensor.Processing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// O Dashboard é outro processo/origem: o navegador só consegue chamar a
// API se ela liberar a origem dele.
var origensPermitidas = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origensPermitidas).AllowAnyMethod()));
builder.Services.AddSingleton<RiscoCache>();
builder.Services.AddSingleton(_ => new RainHistoryStore(builder.Configuration["Estado:HistoricoPath"]));
builder.Services.AddHostedService<RiscoIngestorService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

// Contorno de cada célula é fixo: calcula uma vez e reaproveita.
var contornos = new ConcurrentDictionary<string, double[][]>();

// Resposta em GeoJSON (RFC 7946): uma Feature Polygon por célula, com o
// risco nas properties. atualizado_em e total_celulas são membros extras
// da FeatureCollection, que a RFC permite.
app.MapGet("/risco/atual", (RiscoCache cache) =>
{
    var celulas = cache.LerTodas();
    if (celulas.Count == 0)
    {
        return Results.Json(new { mensagem = "Nenhuma leitura de sensor recebida ainda." }, statusCode: 503);
    }

    var agora = DateTimeOffset.UtcNow;
    return Results.Json(new
    {
        type = "FeatureCollection",
        atualizado_em = celulas.Max(kv => kv.Value.UltimaLeitura),
        total_celulas = celulas.Count,
        features = celulas.Select(kv => new
        {
            type = "Feature",
            geometry = new
            {
                type = "Polygon",
                coordinates = new[] { contornos.GetOrAdd(kv.Key, H3GridBuilder.BoundaryLngLat) },
            },
            properties = new
            {
                h3 = kv.Key,
                municipio = kv.Value.Municipio,
                nivel = kv.Value.Nivel,
                probabilidade = kv.Value.Probabilidade,
                confianca = RiskCalculator.CalcularConfianca(agora, kv.Value.UltimaLeitura),
                ultima_leitura = kv.Value.UltimaLeitura,
                fatores = new { p = kv.Value.P, m = kv.Value.M, s = kv.Value.S },
                chuva_1h_mm = kv.Value.Chuva1HMm,
            },
        }),
    }, contentType: "application/geo+json");
})
.WithName("GetRiscoAtual");

app.Run();

// Exposto pros testes de integração (WebApplicationFactory<Program>).
public partial class Program;
