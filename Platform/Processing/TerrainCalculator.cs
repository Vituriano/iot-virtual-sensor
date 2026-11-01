using System.Text.Json;
using H3Lib;
using H3Lib.Extensions;
using VirtualSensor.Common;
using VirtualSensor.Grid;

namespace VirtualSensor.Processing;

/// <summary>
/// Camada de aplicação: fator de terreno/suscetibilidade (S).
///
/// O documento original pede HAND (altura acima da drenagem mais próxima)
/// derivado do Copernicus GLO-30, combinado com declividade, distância a
/// canal e impermeabilização (MapBiomas). Reconstruir HAND de verdade
/// exige roteamento de fluxo hidrológico (D8, acumulação de fluxo) sobre
/// um raster — fora do escopo aqui. Esta v0.3 usa uma versão simplificada,
/// com dado real mas técnica mais simples:
///
///   - Elevação por célula: Data/elevacao_rmr.json, de open-elevation.com
///     (SRTM 30m — mesma ordem de resolução do Copernicus GLO-30 pedido no
///     documento; usado no lugar dele porque é uma API aberta sem chave e
///     sem precisar de parser de GeoTIFF).
///   - Declividade: proxy calculado a partir da diferença de elevação entre
///     uma célula e seus vizinhos imediatos (H3 k=1), não de um raster de
///     declividade dedicado.
///   - Distância a canal: hidrografia real do OpenStreetMap (rios, riachos,
///     canais e drenos dentro da RMR, via Overpass API), simplificada por
///     Douglas-Peucker; distância da célula ao ponto mais próximo de uma
///     polilinha.
///   - Impermeabilização (MapBiomas): NÃO implementada nesta versão — exige
///     processar um raster de uso do solo, também fora de escopo aqui. S
///     fica sem esse componente por enquanto (documentado como limitação).
///
/// S = média de (cota_norm, declividade_norm, distancia_canal_norm), cada
/// um em [0, 1], onde 1 = mais suscetível (baixo, plano, perto de canal).
/// </summary>
public static class TerrainCalculator
{
    private const decimal CotaBaixaM = 0m;   // nível do mar: cota_norm = 1
    private const decimal CotaAltaM = 30m;   // acima disso: cota_norm = 0

    private const decimal DeclividadeReferencia = 0.05m; // 5%: acima disso, declividade_norm = 0
    private const int H3EdgeLengthM = 461;               // comprimento médio de aresta H3 res 8

    private const decimal DistanciaCanalReferenciaKm = 0.5m; // além disso, distancia_norm = 0

    private sealed record DadosTerreno(
        Dictionary<string, decimal> ElevacaoPorCelula,
        List<(double Lat, double Lng)[]> Hidrografia);

    // Lazy<T> com isThreadSafe:true garante que Carregar() roda uma única
    // vez mesmo se duas requisições chamarem CalcularSuscetibilidade ao
    // mesmo tempo (relevante assim que isto virar API com requests
    // concorrentes, ao contrário do loop single-thread de hoje).
    private static readonly Lazy<DadosTerreno> _dados = new(Carregar, isThreadSafe: true);

    private static DadosTerreno Carregar()
    {
        var dataDir = PathHelpers.SiblingDir("Data");

        Dictionary<string, decimal> elevacao;
        using (var stream = File.OpenRead(Path.Combine(dataDir, "elevacao_rmr.json")))
        using (var doc = JsonDocument.Parse(stream))
        {
            elevacao = new Dictionary<string, decimal>();
            foreach (var prop in doc.RootElement.GetProperty("elevacao_m").EnumerateObject())
            {
                elevacao[prop.Name] = prop.Value.GetDecimal();
            }
        }

        List<(double, double)[]> hidrografia;
        using (var stream = File.OpenRead(Path.Combine(dataDir, "hidrografia_rmr.json")))
        using (var doc = JsonDocument.Parse(stream))
        {
            hidrografia = new List<(double, double)[]>();
            foreach (var linha in doc.RootElement.GetProperty("polilinhas").EnumerateArray())
            {
                var pontos = linha.EnumerateArray()
                    .Select(p => (p[0].GetDouble(), p[1].GetDouble()))
                    .ToArray();
                if (pontos.Length >= 2)
                {
                    hidrografia.Add(pontos);
                }
            }
        }

        return new DadosTerreno(elevacao, hidrografia);
    }

    /// <summary>
    /// Calcula S para cada célula que tenha elevação conhecida. Feito uma
    /// vez no início (referência estática, não muda por ciclo).
    /// </summary>
    public static Dictionary<string, decimal> CalcularSuscetibilidade()
    {
        var dados = _dados.Value;
        var elevacao = dados.ElevacaoPorCelula;

        var resultado = new Dictionary<string, decimal>();
        foreach (var cell in elevacao.Keys)
        {
            var cotaNorm = NormalizarCota(elevacao[cell]);
            var declividadeNorm = NormalizarDeclividade(cell, elevacao);
            var distanciaNorm = NormalizarDistanciaCanal(cell, dados.Hidrografia);

            var s = (cotaNorm + declividadeNorm + distanciaNorm) / 3m;
            resultado[cell] = Math.Clamp(s, 0m, 1m);
        }
        return resultado;
    }

    /// <summary>
    /// Mapeia `valor` linearmente pro intervalo [baixo, alto] em [0, 1] e
    /// clampa. `inverter` troca "quanto maior o valor, maior o resultado"
    /// por "quanto maior o valor, menor o resultado" — usado pelas três
    /// componentes de S, que compartilham essa forma mas cada uma calcula
    /// `valor` de um jeito diferente (elevação direta, diferença entre
    /// vizinhos, distância a hidrografia).
    /// </summary>
    private static decimal NormalizarLinear(decimal valor, decimal baixo, decimal alto, bool inverter)
    {
        var fracao = (valor - baixo) / (alto - baixo);
        return Math.Clamp(inverter ? 1m - fracao : fracao, 0m, 1m);
    }

    private static decimal NormalizarCota(decimal elevacaoM)
        => NormalizarLinear(elevacaoM, CotaBaixaM, CotaAltaM, inverter: true);

    private static decimal NormalizarDeclividade(string cell, Dictionary<string, decimal> elevacao)
    {
        var origin = new H3Index(Convert.ToUInt64(cell, 16));
        Api.KRing(origin, 1, out List<H3Index> vizinhos);

        var elevacoesVizinhas = new List<decimal> { elevacao[cell] };
        foreach (var vizinho in vizinhos)
        {
            var key = H3GridBuilder.CellKey(vizinho);
            if (key != cell && elevacao.TryGetValue(key, out var e))
            {
                elevacoesVizinhas.Add(e);
            }
        }

        if (elevacoesVizinhas.Count < 2)
        {
            // sem vizinho com dado (borda da grade): assume declividade neutra.
            return 0.5m;
        }

        var desnivelM = elevacoesVizinhas.Max() - elevacoesVizinhas.Min();
        var declividade = desnivelM / (2 * H3EdgeLengthM); // rise / run aproximado
        return NormalizarLinear(declividade, 0m, DeclividadeReferencia, inverter: true);
    }

    private static decimal NormalizarDistanciaCanal(string cell, List<(double Lat, double Lng)[]> hidrografia)
    {
        var origin = new H3Index(Convert.ToUInt64(cell, 16));
        var geoCoord = origin.ToGeoCoord();
        var lat = (double)Api.RadiansToDegrees(geoCoord.Latitude);
        var lng = (double)Api.RadiansToDegrees(geoCoord.Longitude);

        var distanciaKm = (decimal)DistanciaMinimaAHidrografiaKm(lat, lng, hidrografia);
        return NormalizarLinear(distanciaKm, 0m, DistanciaCanalReferenciaKm, inverter: true);
    }

    private static double DistanciaMinimaAHidrografiaKm(double lat, double lng, List<(double Lat, double Lng)[]> hidrografia)
    {
        const double kmPorGrauLat = 111.32;
        var kmPorGrauLng = 111.32 * Math.Cos(lat * Math.PI / 180.0);

        var minDist = double.MaxValue;
        foreach (var linha in hidrografia)
        {
            for (var i = 0; i < linha.Length - 1; i++)
            {
                var d = DistanciaPontoSegmentoKm(lat, lng, linha[i], linha[i + 1], kmPorGrauLat, kmPorGrauLng);
                if (d < minDist)
                {
                    minDist = d;
                }
            }
        }
        return minDist;
    }

    private static double DistanciaPontoSegmentoKm(
        double lat, double lng,
        (double Lat, double Lng) a, (double Lat, double Lng) b,
        double kmPorGrauLat, double kmPorGrauLng)
    {
        // projeção planar simples (equirretangular) — suficiente na escala da RMR.
        var px = lng * kmPorGrauLng;
        var py = lat * kmPorGrauLat;
        var ax = a.Lng * kmPorGrauLng;
        var ay = a.Lat * kmPorGrauLat;
        var bx = b.Lng * kmPorGrauLng;
        var by = b.Lat * kmPorGrauLat;

        var dx = bx - ax;
        var dy = by - ay;
        if (dx == 0 && dy == 0)
        {
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        }

        var t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0.0, 1.0);
        var projX = ax + t * dx;
        var projY = ay + t * dy;
        return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }
}
