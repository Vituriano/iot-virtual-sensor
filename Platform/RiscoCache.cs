using VirtualSensor.Processing;

namespace VirtualSensor.Platform;

/// <summary>
/// Guarda o último risco calculado por célula, em memória. Escrito pelo
/// RiscoIngestorService (a cada ciclo novo do sensor) e lido pelo endpoint
/// HTTP — como os dois acontecem em threads diferentes (hosted service +
/// requisições concorrentes), o acesso é protegido por lock.
/// </summary>
public sealed class RiscoCache
{
    private readonly Lock _lock = new();
    private Dictionary<string, RiscoCelula> _riscoPorCelula = new();
    private DateTimeOffset? _ultimoCiclo;

    public void Atualizar(DateTimeOffset ciclo, Dictionary<string, RiscoCelula> riscoPorCelula)
    {
        lock (_lock)
        {
            _ultimoCiclo = ciclo;
            _riscoPorCelula = riscoPorCelula;
        }
    }

    public (DateTimeOffset? Ciclo, Dictionary<string, RiscoCelula> RiscoPorCelula) LerAtual()
    {
        lock (_lock)
        {
            return (_ultimoCiclo, _riscoPorCelula);
        }
    }
}
