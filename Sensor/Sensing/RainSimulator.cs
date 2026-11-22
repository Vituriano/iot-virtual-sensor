using VirtualSensor.Grid;

namespace VirtualSensor.Sensing;

/// <summary>
/// Fonte de chuva sintética e determinística, para testar o fluxo inteiro
/// (Sensor → MQTT → Platform → Dashboard) quando não está chovendo de
/// verdade. Substitui só a leitura do feed APAC/Cemaden: a publicação MQTT,
/// o tópico e o payload são exatamente os mesmos do modo real.
///
/// Cenário: um núcleo de chuva centrado no Recife cuja intensidade segue
/// um perfil de tempestade por ciclo (sobe, atinge o pico, dissipa) e cai
/// com a distância ao centro (gaussiana). Todas as células da grade
/// publicam, inclusive as com 0 mm, pra que o mapa fique completo.
/// </summary>
public static class RainSimulator
{
    // Centro do núcleo de chuva: bairro de Boa Vista, Recife.
    private const double CentroLat = -8.06;
    private const double CentroLng = -34.89;

    // Desvio da gaussiana, em km: ~70% da chuva cai num raio de 8 km.
    private const double SigmaKm = 8.0;

    /// <summary>mm no centro do núcleo a cada ciclo de 15 min.</summary>
    public static readonly decimal[] PerfilTempestadeMm = [2m, 6m, 12m, 20m, 14m, 6m, 2m, 0m];

    public static Dictionary<string, decimal> Gerar(Dictionary<string, CellInfo> grid, int ciclo)
    {
        var pico = PerfilTempestadeMm[ciclo % PerfilTempestadeMm.Length];
        var chuvaPorCelula = new Dictionary<string, decimal>(grid.Count);

        foreach (var h3 in grid.Keys)
        {
            var (lat, lng) = Centro(h3);
            var dKm = DistanciaKm(lat, lng, CentroLat, CentroLng);
            var fator = Math.Exp(-(dKm * dKm) / (2 * SigmaKm * SigmaKm));
            chuvaPorCelula[h3] = Math.Round(pico * (decimal)fator, 1);
        }

        return chuvaPorCelula;
    }

    private static (double Lat, double Lng) Centro(string h3)
    {
        // O anel GeoJSON repete o primeiro vértice no fim; fica de fora da média.
        var contorno = H3GridBuilder.BoundaryLngLat(h3).SkipLast(1).ToArray();
        return (contorno.Average(p => p[1]), contorno.Average(p => p[0]));
    }

    private static double DistanciaKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double raioTerraKm = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * raioTerraKm * Math.Asin(Math.Sqrt(a));
    }
}
