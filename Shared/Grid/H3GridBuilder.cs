using System.Text.Json;
using H3Lib;
using H3Lib.Extensions;
using VirtualSensor.Common;

namespace VirtualSensor.Grid;

public sealed record CellInfo(string Municipio, int CodigoIbge);

/// <summary>
/// Camada de suporte: grade H3 cobrindo os 14 municípios da RMR.
/// Gera a grade uma vez a partir dos polígonos municipais do IBGE
/// (Data/rmr_municipios.json) — não é dado que se busque por API a cada
/// ciclo, é referência estática (fronteira municipal não muda).
/// </summary>
public static class H3GridBuilder
{
    public const int H3Resolution = 8;

    public static Dictionary<string, CellInfo> BuildGrid()
    {
        var municipiosPath = Path.Combine(PathHelpers.SiblingDir("Data"), "rmr_municipios.json");
        using var stream = File.OpenRead(municipiosPath);
        using var doc = JsonDocument.Parse(stream);

        var cellToMunicipio = new Dictionary<string, CellInfo>();

        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            var nome = props.GetProperty("municipio").GetString()!;
            var codigo = props.GetProperty("codigo_ibge").GetInt32();
            var geometry = feature.GetProperty("geometry");
            var geomType = geometry.GetProperty("type").GetString();

            foreach (var polygon in ExtractGeoPolygons(geometry, geomType!))
            {
                Api.PolyFill(polygon, H3Resolution, out List<H3Index> cells);
                foreach (var cell in cells)
                {
                    var key = CellKey(cell);
                    // Uma célula pode tocar mais de um município (borda);
                    // fica com o primeiro município que a reivindicar.
                    cellToMunicipio.TryAdd(key, new CellInfo(nome, codigo));
                }
            }
        }

        return cellToMunicipio;
    }

    /// <summary>
    /// Nomes dos 14 municípios da RMR, direto de Data/rmr_municipios.json —
    /// fonte única (CemadenClient usava uma lista fixa à parte antes, o que
    /// exigia editar dois lugares se um município mudasse de nome).
    /// </summary>
    public static HashSet<string> CarregarNomesMunicipios()
    {
        var municipiosPath = Path.Combine(PathHelpers.SiblingDir("Data"), "rmr_municipios.json");
        using var stream = File.OpenRead(municipiosPath);
        using var doc = JsonDocument.Parse(stream);

        var nomes = new HashSet<string>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var nome = feature.GetProperty("properties").GetProperty("municipio").GetString()!;
            nomes.Add(nome.ToUpperInvariant());
        }
        return nomes;
    }

    public static string CellKey(H3Index index) => index.Value.ToString("x");

    public static string LatLngToCellKey(decimal lat, decimal lng)
    {
        var gc = GeoCoordExtensions.SetDegrees(new GeoCoord(), lat, lng);
        var idx = Api.GeoToH3(gc, H3Resolution);
        return CellKey(idx);
    }

    /// <summary>
    /// GeoJSON usa (lng, lat); H3Lib espera GeoCoord em graus (lat, lng).
    /// Suporta Polygon e MultiPolygon (ex.: Ipojuca).
    /// </summary>
    private static IEnumerable<GeoPolygon> ExtractGeoPolygons(JsonElement geometry, string geomType)
    {
        var coordinates = geometry.GetProperty("coordinates");

        if (geomType == "Polygon")
        {
            yield return RingsToGeoPolygon(coordinates);
        }
        else if (geomType == "MultiPolygon")
        {
            foreach (var polygonCoords in coordinates.EnumerateArray())
            {
                yield return RingsToGeoPolygon(polygonCoords);
            }
        }
        else
        {
            throw new NotSupportedException($"Tipo de geometria não suportado: {geomType}");
        }
    }

    private static GeoPolygon RingsToGeoPolygon(JsonElement rings)
    {
        var ringsList = rings.EnumerateArray().ToList();

        var outerRing = RingToVerts(ringsList[0]);
        var geoPolygon = new GeoPolygon
        {
            GeoFence = new GeoFence { NumVerts = outerRing.Length, Verts = outerRing },
            NumHoles = ringsList.Count - 1,
            Holes = ringsList.Skip(1).Select(r =>
            {
                var verts = RingToVerts(r);
                return new GeoFence { NumVerts = verts.Length, Verts = verts };
            }).ToList()
        };

        return geoPolygon;
    }

    private static GeoCoord[] RingToVerts(JsonElement ring)
    {
        return ring.EnumerateArray()
            .Select(point =>
            {
                var arr = point.EnumerateArray().ToArray();
                var lng = arr[0].GetDecimal();
                var lat = arr[1].GetDecimal();
                return GeoCoordExtensions.SetDegrees(new GeoCoord(), lat, lng);
            })
            .ToArray();
    }
}
