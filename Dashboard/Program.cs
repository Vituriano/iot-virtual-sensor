// Dashboard: serve só a página estática (mapa H3). Quem fala com a
// Platform é o navegador, por HTTP; este processo não conhece o risco.
// A URL da Platform vem de config e é entregue à página em /config.json.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/config.json", (IConfiguration config) => Results.Ok(new
{
    platformUrl = config["PlatformUrl"] ?? "http://localhost:5128",
}));

app.Run();
