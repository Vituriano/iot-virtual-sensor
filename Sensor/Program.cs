using VirtualSensor.Sensor;

// --once e --simular são flags sem valor (lidas pelo Worker). Ficam fora da
// configuração: o parser trataria o argumento seguinte como valor delas
// (ex.: "--simular --Sensor:IntervaloSegundos=60" perderia o intervalo).
string[] flags = ["--once", "--simular"];
var builder = Host.CreateApplicationBuilder(args.Except(flags).ToArray());
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
