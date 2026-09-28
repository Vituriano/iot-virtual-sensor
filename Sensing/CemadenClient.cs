using System.Text.Json;
using VirtualSensor.Grid;

namespace VirtualSensor.Sensing;

public sealed record Leitura(decimal Lat, decimal Lng, string Cidade, decimal ChuvaMm, string? NomeEstacao);

/// <summary>
/// Parte da camada de sensoriamento: em vez de ler um pino físico, cada
/// célula/sensor consome o feed público mantido pela APAC/Cemaden — este é
/// o único dado que muda em tempo real, por isso é buscado por HTTP a cada
/// ciclo (ao contrário da grade H3, que é referência estática em Data/).
/// </summary>
public sealed class CemadenClient
{
    private const string CemadenUrl = "http://dados.apac.pe.gov.br:41120/cemaden/";

    // Carregado de Data/rmr_municipios.json (mesma fonte que a grade H3) em
    // vez de uma lista fixa aqui — assim só existe um lugar pra atualizar
    // se um município mudar de nome ou entrar/sair da RMR.
    private static readonly Lazy<HashSet<string>> RmrMunicipios = new(H3GridBuilder.CarregarNomesMunicipios, isThreadSafe: true);

    private readonly HttpClient _http;

    public CemadenClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<List<Leitura>> LeiturasRmrAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(CemadenUrl, ct);
        using var doc = JsonDocument.Parse(json);

        var leituras = new List<Leitura>();
        foreach (var record in doc.RootElement.EnumerateArray())
        {
            var leitura = ParseReading(record);
            if (leitura is not null && RmrMunicipios.Value.Contains(leitura.Cidade))
            {
                leituras.Add(leitura);
            }
        }
        return leituras;
    }

    /// <summary>
    /// Extrai (lat, lng, cidade, chuva) do campo aninhado "Dados_completos".
    /// Retorna null se a estação não tiver coordenada nesse feed (limitação
    /// conhecida: estações só-APAC não trazem latitude/longitude) ou não
    /// tiver leitura de chuva.
    /// </summary>
    private static Leitura? ParseReading(JsonElement record)
    {
        if (!record.TryGetProperty("Dados_completos", out var rawProp))
        {
            return null;
        }

        JsonDocument dadosDoc;
        try
        {
            dadosDoc = JsonDocument.Parse(rawProp.GetString() ?? "{}");
        }
        catch (JsonException)
        {
            return null;
        }

        using var _ = dadosDoc;
        var dados = dadosDoc.RootElement;

        if (!TryGetDecimal(dados, "latitude", out var lat) || !TryGetDecimal(dados, "longitude", out var lng))
        {
            return null;
        }

        if (!TryGetDecimal(dados, "chuva", out var chuva))
        {
            return null;
        }

        var cidade = dados.TryGetProperty("cidade", out var cidadeProp)
            ? (cidadeProp.GetString() ?? "").Trim().ToUpperInvariant()
            : "";

        var nome = dados.TryGetProperty("nome", out var nomeProp) ? nomeProp.GetString() : null;

        return new Leitura(lat, lng, cidade, chuva, nome);
    }

    private static bool TryGetDecimal(JsonElement obj, string prop, out decimal value)
    {
        value = 0;
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(el.GetString(), out value),
            _ => false,
        };
    }
}
