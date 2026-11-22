using System.Collections.Concurrent;
using VirtualSensor.Processing;

namespace VirtualSensor.Platform;

/// <summary>
/// Guarda o último risco calculado por célula, em memória. Cada sensor
/// atualiza só a sua própria entrada (RiscoIngestorService, a cada
/// mensagem MQTT) enquanto o endpoint HTTP lê tudo — por isso o
/// dicionário concorrente, sem lock global.
/// </summary>
public sealed class RiscoCache
{
    private readonly ConcurrentDictionary<string, RiscoCelula> _riscoPorCelula = new();

    public void Atualizar(string celula, RiscoCelula risco) => _riscoPorCelula[celula] = risco;

    public IReadOnlyList<KeyValuePair<string, RiscoCelula>> LerTodas() => _riscoPorCelula.ToArray();
}
