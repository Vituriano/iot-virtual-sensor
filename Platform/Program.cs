// Plataforma IoT: recebe a chuva publicada pelo Sensor (via LeituraStore),
// cruza com maré e terreno (dado estático), calcula o risco por célula e
// expõe isso por HTTP. Roda como processo separado do Sensor — os dois só
// se falam através do arquivo compartilhado em estado-sensor/.

using VirtualSensor.Platform;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<RiscoCache>();
builder.Services.AddHostedService<RiscoIngestorService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/risco/atual", (RiscoCache cache) =>
{
    var (ciclo, riscoPorCelula) = cache.LerAtual();
    if (ciclo is null)
    {
        return Results.Json(new { mensagem = "Nenhum ciclo do sensor recebido ainda." }, statusCode: 503);
    }

    return Results.Ok(new
    {
        ciclo,
        total_celulas = riscoPorCelula.Count,
        celulas = riscoPorCelula.Select(kv => new
        {
            h3 = kv.Key,
            municipio = kv.Value.Municipio,
            nivel = kv.Value.Nivel,
            probabilidade = kv.Value.Probabilidade,
            confianca = kv.Value.Confianca,
            fatores = new { p = kv.Value.P, m = kv.Value.M, s = kv.Value.S },
            chuva_1h_mm = kv.Value.Chuva1HMm,
        }),
    });
})
.WithName("GetRiscoAtual");

app.Run();
