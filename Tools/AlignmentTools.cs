using System.ComponentModel;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using Civil3dMcp.Infrastructure;
using Civil3dMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3dMcp.Tools;

[McpServerToolType]
public static class AlignmentTools
{
    internal static Alignment Find(DocScope s, string name) =>
        Info.ByName<Alignment>(s.Tr, s.Civil.GetAlignmentIds(), name, "Alinhamento");

    internal static object Describe(DocScope s, Alignment a, bool details)
    {
        var profiles = Info.Open<Profile>(s.Tr, a.GetProfileIds()).Select(p => new
        {
            handle = p.Handle.ToString(), name = p.Name, type = p.ProfileType.ToString(),
        }).ToList();

        object? entities = null;
        if (details)
        {
            entities = Enumerable.Range(0, a.Entities.Count).Select(i =>
            {
                var e = a.Entities[i];
                return new
                {
                    index = i,
                    type = e.EntityType.ToString(),
                    startStation = Fmt.R(Safe(() => SubStart(e))),
                    endStation = Fmt.R(Safe(() => SubEnd(e))),
                    length = Fmt.R(Safe(() => SubLength(e))),
                };
            }).ToList();
        }

        return new
        {
            handle = a.Handle.ToString(),
            name = a.Name,
            description = a.Description,
            type = a.AlignmentType.ToString(),
            style = Info.StyleName(s.Tr, a),
            layer = a.Layer,
            site = a.SiteId.IsNull ? null : s.Get<Site>(a.SiteId).Name,
            length = Fmt.R(a.Length),
            startStation = Fmt.R(a.StartingStation),
            endStation = Fmt.R(a.EndingStation),
            entityCount = a.Entities.Count,
            profiles,
            entities,
        };
    }

    private static double SubStart(AlignmentEntity e) => e.SubEntityCount > 0 ? e[0].StartStation : double.NaN;
    private static double SubEnd(AlignmentEntity e) => e.SubEntityCount > 0 ? e[e.SubEntityCount - 1].EndStation : double.NaN;
    private static double SubLength(AlignmentEntity e)
    {
        double total = 0;
        for (var i = 0; i < e.SubEntityCount; i++) total += e[i].Length;
        return total;
    }
    private static double Safe(Func<double> f) { try { return f(); } catch { return double.NaN; } }

    [McpServerTool(Name = "list_alignments", ReadOnly = true), Description(
        "Lista os alinhamentos (com e sem site): comprimento, estacas inicial/final, estilo e perfis associados.")]
    public static Task<CallToolResult> ListAlignments() =>
        ToolInvoker.Read("list_alignments", null, s =>
            Info.Open<Alignment>(s.Tr, s.Civil.GetAlignmentIds()).Select(a => Describe(s, a, false)).ToList());

    [McpServerTool(Name = "get_alignment", ReadOnly = true), Description(
        "Detalhes de um alinhamento, incluindo a lista de elementos (tangente, curva, espiral) com estacas e comprimentos.")]
    public static Task<CallToolResult> GetAlignment([Description("Nome do alinhamento.")] string name) =>
        ToolInvoker.Read("get_alignment", new { name }, s => Describe(s, Find(s, name), true));

    [McpServerTool(Name = "alignment_station_offset", ReadOnly = true), Description(
        "Converte pontos XY em estaca e afastamento (offset; positivo à direita) em relação a um alinhamento.")]
    public static Task<CallToolResult> StationOffset(
        [Description("Nome do alinhamento.")] string alignment,
        [Description("Pontos XY.")] P3[] points) =>
        ToolInvoker.Read("alignment_station_offset", new { alignment, points }, s =>
        {
            var a = Find(s, alignment);
            return points.Select(p =>
            {
                double st = 0, off = 0;
                string? error = null;
                try { a.StationOffset(p.X, p.Y, ref st, ref off); }
                catch (Exception ex) { error = ex.Message; }
                return new { x = p.X, y = p.Y, station = error is null ? Fmt.R(st) : (double?)null, offset = error is null ? Fmt.R(off) : (double?)null, error };
            }).ToList();
        });

    [McpServerTool(Name = "alignment_point_at_station", ReadOnly = true), Description(
        "Coordenadas XY de pontos dados por estaca e offset (positivo à direita) ao longo de um alinhamento, com a direção (azimute em graus) na estaca.")]
    public static Task<CallToolResult> PointAtStation(
        [Description("Nome do alinhamento.")] string alignment,
        [Description("Estacas (valor numérico, ex.: 120.5).")] double[] stations,
        [Description("Offset aplicado a todas as estacas.")] double offset = 0) =>
        ToolInvoker.Read("alignment_point_at_station", new { alignment, stations, offset }, s =>
        {
            var a = Find(s, alignment);
            return stations.Select(st =>
            {
                double x = 0, y = 0, bearing = 0;
                string? error = null;
                try
                {
                    a.PointLocation(st, offset, 0, ref x, ref y, ref bearing);
                }
                catch (Exception ex) { error = ex.Message; }
                return new
                {
                    station = st,
                    offset,
                    x = error is null ? Fmt.R(x) : (double?)null,
                    y = error is null ? Fmt.R(y) : (double?)null,
                    directionDeg = error is null ? Fmt.R(bearing * 180 / Math.PI, 6) : (double?)null,
                    error,
                };
            }).ToList();
        });

    [McpServerTool(Name = "create_alignment_from_polyline"), Description(
        "Cria um alinhamento a partir de uma polilinha (handle). Por padrão sem site, com o primeiro estilo e conjunto de rótulos do desenho.")]
    public static Task<CallToolResult> CreateFromPolyline(
        [Description("Handle da polilinha.")] string polylineHandle,
        [Description("Nome do alinhamento.")] string name,
        [Description("Estilo de alinhamento.")] string? style = null,
        [Description("Conjunto de rótulos (label set).")] string? labelSet = null,
        [Description("Layer (padrão: layer atual).")] string? layer = null,
        [Description("Nome do site (padrão: sem site).")] string? site = null,
        [Description("Inserir curvas entre tangentes.")] bool addCurves = false,
        [Description("Apagar a polilinha original.")] bool eraseSource = false) =>
        ToolInvoker.Write("create_alignment_from_polyline", new { polylineHandle, name, style, labelSet, layer, site }, s =>
        {
            var plId = s.IdFromHandle(polylineHandle);
            var st = s.Civil.Styles;
            var styleId = Info.StyleId(s.Tr, st.AlignmentStyles, style, "AlignmentStyles");
            var labelId = Info.StyleId(s.Tr, st.LabelSetStyles.AlignmentLabelSetStyles, labelSet, "AlignmentLabelSetStyles");
            var siteId = ObjectId.Null;
            if (!string.IsNullOrWhiteSpace(site))
                siteId = Info.Open<Site>(s.Tr, s.Civil.GetSiteIds()).FirstOrDefault(x => string.Equals(x.Name, site, StringComparison.OrdinalIgnoreCase))?.ObjectId
                         ?? throw new ArgumentException($"Site '{site}' não encontrado.");
            var opts = new PolylineOptions { PlineId = plId, AddCurvesBetweenTangents = addCurves, EraseExistingEntities = eraseSource };
            var id = Alignment.Create(s.Civil, opts, name, siteId, s.LayerId(layer), styleId, labelId);
            return Describe(s, s.Get<Alignment>(id), true);
        });

    // ------------------------------------------------------------------ perfis

    internal static Profile FindProfile(DocScope s, Alignment a, string name) =>
        Info.ByName<Profile>(s.Tr, a.GetProfileIds(), name, $"Perfil (do alinhamento '{a.Name}')");

    private static object DescribeProfile(DocScope s, Profile p) => new
    {
        handle = p.Handle.ToString(),
        name = p.Name,
        type = p.ProfileType.ToString(),
        style = Info.StyleName(s.Tr, p),
        startStation = Fmt.R(Safe(() => p.StartingStation)),
        endStation = Fmt.R(Safe(() => p.EndingStation)),
        minElevation = Fmt.R(Safe(() => p.ElevationMin)),
        maxElevation = Fmt.R(Safe(() => p.ElevationMax)),
        pvis = SafeCount(() => p.PVIs.Count),
    };

    private static int? SafeCount(Func<int> f) { try { return f(); } catch { return null; } }

    [McpServerTool(Name = "list_profiles", ReadOnly = true), Description(
        "Lista os perfis longitudinais de um alinhamento (de superfície, de projeto, etc.) com estacas, cotas mín/máx e nº de PVIs.")]
    public static Task<CallToolResult> ListProfiles([Description("Nome do alinhamento.")] string alignment) =>
        ToolInvoker.Read("list_profiles", new { alignment }, s =>
        {
            var a = Find(s, alignment);
            return Info.Open<Profile>(s.Tr, a.GetProfileIds()).Select(p => DescribeProfile(s, p)).ToList();
        });

    [McpServerTool(Name = "get_profile_elevations", ReadOnly = true), Description(
        "Cota de um perfil em estacas dadas. Se 'interval' for informado (e 'stations' vazio), amostra o perfil inteiro nesse intervalo.")]
    public static Task<CallToolResult> GetProfileElevations(
        [Description("Nome do alinhamento.")] string alignment,
        [Description("Nome do perfil.")] string profile,
        [Description("Estacas.")] double[]? stations = null,
        [Description("Intervalo de amostragem (ex.: 20).")] double? interval = null) =>
        ToolInvoker.Read("get_profile_elevations", new { alignment, profile, stations, interval }, s =>
        {
            var a = Find(s, alignment);
            var p = FindProfile(s, a, profile);
            var list = stations?.ToList() ?? [];
            if (list.Count == 0)
            {
                if (interval is not > 0) throw new ArgumentException("Informe 'stations' ou um 'interval' > 0.");
                var start = p.StartingStation;
                var end = p.EndingStation;
                if ((end - start) / interval.Value > 5000) throw new ArgumentException("Intervalo pequeno demais (mais de 5000 amostras).");
                for (var st = start; st < end; st += interval.Value) list.Add(st);
                list.Add(end);
            }
            return list.Select(st =>
            {
                double? z = null; string? error = null;
                try { z = Fmt.R(p.ElevationAt(st)); } catch (Exception ex) { error = ex.Message; }
                return new { station = Fmt.R(st), elevation = z, error };
            }).ToList();
        });

    [McpServerTool(Name = "create_profile_from_surface"), Description(
        "Cria o perfil do terreno (perfil de superfície) de um alinhamento sobre uma superfície.")]
    public static Task<CallToolResult> CreateProfileFromSurface(
        [Description("Nome do alinhamento.")] string alignment,
        [Description("Nome da superfície.")] string surface,
        [Description("Nome do novo perfil (padrão: '<alinhamento> - <superfície>').")] string? name = null,
        [Description("Estilo de perfil.")] string? style = null,
        [Description("Conjunto de rótulos de perfil.")] string? labelSet = null,
        [Description("Layer.")] string? layer = null) =>
        ToolInvoker.Write("create_profile_from_surface", new { alignment, surface, name, style, labelSet, layer }, s =>
        {
            var a = Find(s, alignment);
            var srf = SurfaceTools.Find(s, surface);
            var st = s.Civil.Styles;
            var styleId = Info.StyleId(s.Tr, st.ProfileStyles, style, "ProfileStyles");
            var labelId = Info.StyleId(s.Tr, st.LabelSetStyles.ProfileLabelSetStyles, labelSet, "ProfileLabelSetStyles");
            var id = Profile.CreateFromSurface(name ?? $"{a.Name} - {srf.Name}", a.ObjectId, srf.ObjectId, s.LayerId(layer), styleId, labelId);
            return DescribeProfile(s, s.Get<Profile>(id));
        }, 300_000);

    [McpServerTool(Name = "create_profile_view"), Description(
        "Cria uma vista de perfil (profile view) do alinhamento no ponto de inserção dado, mostrando seus perfis.")]
    public static Task<CallToolResult> CreateProfileView(
        [Description("Nome do alinhamento.")] string alignment,
        [Description("Ponto de inserção (canto inferior esquerdo).")] P3 insertionPoint,
        [Description("Nome da vista (padrão: automático).")] string? name = null,
        [Description("Estilo da vista de perfil.")] string? style = null,
        [Description("Conjunto de bandas.")] string? bandSet = null) =>
        ToolInvoker.Write("create_profile_view", new { alignment, insertionPoint, name, style, bandSet }, s =>
        {
            var a = Find(s, alignment);
            var st = s.Civil.Styles;
            var styleId = Info.StyleId(s.Tr, st.ProfileViewStyles, style, "ProfileViewStyles");
            var bandId = Info.StyleId(s.Tr, st.ProfileViewBandSetStyles, bandSet, "ProfileViewBandSetStyles");
            var id = ProfileView.Create(a.ObjectId, insertionPoint.ToPoint(), name ?? $"PV - {a.Name}", bandId, styleId);
            var pv = s.Get<ProfileView>(id);
            return new { handle = pv.Handle.ToString(), name = pv.Name, location = P3.Of(pv.Location) };
        }, 300_000);
}
