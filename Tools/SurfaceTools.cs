using System.ComponentModel;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using Civil3dMcp.Infrastructure;
using Civil3dMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3dMcp.Tools;

[McpServerToolType]
public static class SurfaceTools
{
    internal static CivSurface Find(DocScope s, string name) =>
        Info.ByName<CivSurface>(s.Tr, s.Civil.GetSurfaceIds(), name, "Superfície");

    internal static object Describe(DocScope s, CivSurface srf, bool details)
    {
        object? stats = null;
        try
        {
            var g = srf.GetGeneralProperties();
            stats = new
            {
                minElevation = Fmt.R(g.MinimumElevation),
                maxElevation = Fmt.R(g.MaximumElevation),
                meanElevation = Fmt.R(g.MeanElevation),
                numberOfPoints = g.NumberOfPoints,
                min = new { x = Fmt.R(g.MinimumCoordinateX), y = Fmt.R(g.MinimumCoordinateY) },
                max = new { x = Fmt.R(g.MaximumCoordinateX), y = Fmt.R(g.MaximumCoordinateY) },
            };
        }
        catch { /* superfície vazia */ }

        object? tin = null;
        if (details && srf is TinSurface ts)
        {
            try
            {
                var t = ts.GetTinProperties();
                tin = Info.PlainProperties(t);
            }
            catch { }
        }

        object? volume = null;
        if (srf is TinVolumeSurface vs)
        {
            try
            {
                var v = vs.GetVolumeProperties();
                volume = new
                {
                    cut = Fmt.R(v.UnadjustedCutVolume), fill = Fmt.R(v.UnadjustedFillVolume), net = Fmt.R(v.UnadjustedNetVolume),
                    adjustedCut = Fmt.R(v.AdjustedCutVolume), adjustedFill = Fmt.R(v.AdjustedFillVolume), adjustedNet = Fmt.R(v.AdjustedNetVolume),
                    cutFactor = v.CutFactor, fillFactor = v.FillFactor,
                };
            }
            catch { }
        }

        return new
        {
            handle = srf.Handle.ToString(),
            name = srf.Name,
            type = srf.GetType().Name,
            description = srf.Description,
            style = Info.StyleName(s.Tr, srf),
            layer = srf.Layer,
            isOutOfDate = srf.IsOutOfDate,
            stats,
            tin,
            volume,
        };
    }

    [McpServerTool(Name = "list_surfaces", ReadOnly = true), Description(
        "Lista as superfícies do desenho (TIN, grid, volume) com cotas mín/máx/média, nº de pontos e extents.")]
    public static Task<CallToolResult> ListSurfaces() =>
        ToolInvoker.Read("list_surfaces", null, s =>
            Info.Open<CivSurface>(s.Tr, s.Civil.GetSurfaceIds()).Select(x => Describe(s, x, false)).ToList());

    [McpServerTool(Name = "get_surface", ReadOnly = true), Description(
        "Detalhes de uma superfície: estatísticas gerais, propriedades TIN (triângulos, áreas 2D/3D) e volumes, se for superfície de volume.")]
    public static Task<CallToolResult> GetSurface([Description("Nome da superfície.")] string name) =>
        ToolInvoker.Read("get_surface", new { name }, s => Describe(s, Find(s, name), true));

    [McpServerTool(Name = "get_surface_elevations", ReadOnly = true), Description(
        "Cota da superfície em um ou mais pontos XY. Pontos fora da superfície voltam com elevation nulo.")]
    public static Task<CallToolResult> GetSurfaceElevations(
        [Description("Nome da superfície.")] string surface,
        [Description("Pontos XY (Z é ignorado).")] P3[] points) =>
        ToolInvoker.Read("get_surface_elevations", new { surface, points }, s =>
        {
            var srf = Find(s, surface);
            return points.Select(p =>
            {
                double? z = null;
                string? error = null;
                try { z = Fmt.R(srf.FindElevationAtXY(p.X, p.Y)); }
                catch (Exception ex) { error = ex.Message; }
                return new { x = p.X, y = p.Y, elevation = z, error };
            }).ToList();
        });

    [McpServerTool(Name = "create_tin_surface"), Description(
        "Cria uma superfície TIN vazia (opcionalmente já com pontos). Depois use add_surface_data para pontos, linhas de quebra, curvas de nível ou grupos de pontos.")]
    public static Task<CallToolResult> CreateTinSurface(
        [Description("Nome da nova superfície.")] string name,
        [Description("Estilo de superfície (padrão: o primeiro do desenho).")] string? style = null,
        [Description("Descrição.")] string? description = null,
        [Description("Pontos XYZ iniciais (opcional).")] P3[]? points = null,
        [Description("Layer (opcional).")] string? layer = null) =>
        ToolInvoker.Write("create_tin_surface", new { name, style, description, pointCount = points?.Length, layer }, s =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nome obrigatório.");
            if (Info.Open<CivSurface>(s.Tr, s.Civil.GetSurfaceIds()).Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Já existe uma superfície '{name}'.");
            var styleId = Info.StyleId(s.Tr, s.Civil.Styles.SurfaceStyles, style, "SurfaceStyles");
            var id = TinSurface.Create(name, styleId);
            var srf = s.Get<TinSurface>(id, OpenMode.ForWrite);
            if (description is not null) srf.Description = description;
            if (!string.IsNullOrWhiteSpace(layer)) srf.Layer = s.EnsureLayer(layer);
            if (points is { Length: > 0 })
                srf.AddVertices(new Point3dCollection(points.Select(p => p.ToPoint()).ToArray()));
            return Describe(s, srf, true);
        });

    [McpServerTool(Name = "add_surface_data"), Description(
        "Adiciona dados a uma superfície TIN: vértices XYZ, linhas de quebra (handles de polilinhas 3D/2D, linhas de recurso), curvas de nível (handles de polilinhas com elevação) e/ou grupo de pontos COGO. Reconstrói a superfície ao final.")]
    public static Task<CallToolResult> AddSurfaceData(
        [Description("Nome da superfície TIN.")] string surface,
        [Description("Vértices XYZ a adicionar.")] P3[]? points = null,
        [Description("Handles das entidades a usar como linhas de quebra padrão.")] string[]? breaklineHandles = null,
        [Description("Handles das polilinhas a usar como curvas de nível.")] string[]? contourHandles = null,
        [Description("Nome do grupo de pontos COGO a adicionar.")] string? pointGroup = null,
        [Description("Distância máxima de suplementação para linhas de quebra/curvas (0 = sem).")] double maxDistance = 0,
        [Description("Descrição da operação no histórico da superfície.")] string? label = null) =>
        ToolInvoker.Write("add_surface_data", new { surface, points = points?.Length, breaklineHandles, contourHandles, pointGroup }, s =>
        {
            var srf = Find(s, surface) as TinSurface ?? throw new ArgumentException($"'{surface}' não é uma superfície TIN.");
            srf.UpgradeOpen();
            var done = new List<string>();
            if (points is { Length: > 0 })
            {
                srf.AddVertices(new Point3dCollection(points.Select(p => p.ToPoint()).ToArray()));
                done.Add($"{points.Length} vértices");
            }
            if (breaklineHandles is { Length: > 0 })
            {
                var ids = new ObjectIdCollection(breaklineHandles.Select(s.IdFromHandle).ToArray());
                var op = srf.BreaklinesDefinition.AddStandardBreaklines(ids, 1.0, maxDistance, 0, 0);
                if (label is not null) op.Description = label;
                done.Add($"{ids.Count} linhas de quebra");
            }
            if (contourHandles is { Length: > 0 })
            {
                var ids = new ObjectIdCollection(contourHandles.Select(s.IdFromHandle).ToArray());
                var op = srf.ContoursDefinition.AddContours(ids, 1.0, maxDistance, 0, 0);
                if (label is not null) op.Description = label;
                done.Add($"{ids.Count} curvas de nível");
            }
            if (!string.IsNullOrWhiteSpace(pointGroup))
            {
                var pg = PointTools.FindGroup(s, pointGroup);
                srf.PointGroupsDefinition.AddPointGroup(pg.ObjectId);
                done.Add($"grupo de pontos '{pg.Name}'");
            }
            if (done.Count == 0) throw new ArgumentException("Nada a adicionar: informe points, breaklineHandles, contourHandles ou pointGroup.");
            srf.Rebuild();
            return new { added = done, surface = Describe(s, srf, true) };
        }, 300_000);

    [McpServerTool(Name = "create_volume_surface"), Description(
        "Cria uma superfície de volume TIN (comparação base x comparação) e devolve corte, aterro e volume líquido (unidades cúbicas do desenho).")]
    public static Task<CallToolResult> CreateVolumeSurface(
        [Description("Nome da superfície de volume.")] string name,
        [Description("Superfície base (ex.: terreno existente).")] string baseSurface,
        [Description("Superfície de comparação (ex.: projeto).")] string comparisonSurface,
        [Description("Estilo (padrão: o primeiro).")] string? style = null) =>
        ToolInvoker.Write("create_volume_surface", new { name, baseSurface, comparisonSurface, style }, s =>
        {
            var b = Find(s, baseSurface);
            var c = Find(s, comparisonSurface);
            var styleId = Info.StyleId(s.Tr, s.Civil.Styles.SurfaceStyles, style, "SurfaceStyles");
            var id = TinVolumeSurface.Create(name, b.ObjectId, c.ObjectId, styleId);
            return Describe(s, s.Get<TinVolumeSurface>(id), true);
        }, 300_000);

    [McpServerTool(Name = "rebuild_surface"), Description("Reconstrói uma superfície desatualizada.")]
    public static Task<CallToolResult> RebuildSurface([Description("Nome da superfície.")] string name) =>
        ToolInvoker.Write("rebuild_surface", new { name }, s =>
        {
            var srf = Find(s, name);
            srf.UpgradeOpen();
            srf.Rebuild();
            return Describe(s, srf, true);
        }, 300_000);
}
