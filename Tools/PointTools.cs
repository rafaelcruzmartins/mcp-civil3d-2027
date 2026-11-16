using System.ComponentModel;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using Civil3dMcp.Infrastructure;
using Civil3dMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Civil3dMcp.Tools;

[McpServerToolType]
public static class PointTools
{
    internal static PointGroup FindGroup(DocScope s, string name) =>
        Info.Open<PointGroup>(s.Tr, s.Civil.PointGroups).FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ToolException($"Grupo de pontos '{name}' não encontrado.",
            new { available = Info.Open<PointGroup>(s.Tr, s.Civil.PointGroups).Select(g => g.Name).ToList() });

    private static object Describe(CogoPoint p) => new
    {
        handle = p.Handle.ToString(),
        number = p.PointNumber,
        name = p.PointName,
        description = p.RawDescription,
        fullDescription = p.FullDescription,
        easting = Fmt.R(p.Easting),
        northing = Fmt.R(p.Northing),
        elevation = Fmt.R(p.Elevation),
        layer = p.Layer,
    };

    [McpServerTool(Name = "list_cogo_points", ReadOnly = true), Description(
        "Lista pontos COGO (número, nome, descrição, E/N/cota). Filtros opcionais: grupo de pontos e texto contido na descrição.")]
    public static Task<CallToolResult> ListCogoPoints(
        [Description("Nome do grupo de pontos.")] string? pointGroup = null,
        [Description("Texto contido na descrição bruta (sem distinção de maiúsculas).")] string? descriptionContains = null,
        [Description("Máximo de itens (padrão 500).")] int limit = 500) =>
        ToolInvoker.Read("list_cogo_points", new { pointGroup, descriptionContains, limit }, s =>
        {
            IEnumerable<ObjectId> ids = s.Civil.CogoPoints;
            if (pointGroup is not null)
            {
                var g = FindGroup(s, pointGroup);
                var nums = g.GetPointNumbers();
                ids = nums.Select(n => s.Civil.CogoPoints.GetPointByPointNumber(n));
            }
            var pts = Info.Open<CogoPoint>(s.Tr, ids.ToList());
            if (descriptionContains is not null)
                pts = pts.Where(p => (p.RawDescription ?? "").Contains(descriptionContains, StringComparison.OrdinalIgnoreCase));
            var all = pts.ToList();
            return new { total = all.Count, returned = Math.Min(all.Count, limit), points = all.Take(limit).Select(Describe).ToList() };
        });

    [McpServerTool(Name = "create_cogo_points"), Description(
        "Cria pontos COGO (usa a numeração automática do desenho). Opcionalmente adiciona os pontos a um grupo de pontos novo ou existente (por número).")]
    public static Task<CallToolResult> CreateCogoPoints(
        [Description("Pontos a criar.")] CogoInput[] points,
        [Description("Grupo de pontos ao qual incluir (criado se não existir).")] string? pointGroup = null) =>
        ToolInvoker.Write("create_cogo_points", new { count = points?.Length, pointGroup }, s =>
        {
            if (points is null || points.Length == 0) throw new ArgumentException("Informe ao menos um ponto.");
            var created = new List<CogoPoint>();
            foreach (var p in points)
            {
                var id = s.Civil.CogoPoints.Add(new Point3d(p.X, p.Y, p.Z), p.Description ?? "", true);
                var cp = s.Get<CogoPoint>(id, OpenMode.ForWrite);
                if (!string.IsNullOrWhiteSpace(p.Name)) cp.PointName = p.Name;
                created.Add(cp);
            }
            if (!string.IsNullOrWhiteSpace(pointGroup))
            {
                PointGroup g;
                if (s.Civil.PointGroups.Contains(pointGroup))
                    g = s.Get<PointGroup>(s.Civil.PointGroups[pointGroup], OpenMode.ForWrite);
                else
                    g = s.Get<PointGroup>(s.Civil.PointGroups.Add(pointGroup), OpenMode.ForWrite);
                var q = g.GetQuery() as StandardPointGroupQuery ?? new StandardPointGroupQuery();
                var list = string.Join(",", created.Select(c => c.PointNumber));
                q.IncludeNumbers = string.IsNullOrWhiteSpace(q.IncludeNumbers) ? list : q.IncludeNumbers + "," + list;
                g.SetQuery(q);
                g.Update();
            }
            return new { created = created.Count, points = created.Select(Describe).ToList() };
        });

    [McpServerTool(Name = "list_point_groups", ReadOnly = true), Description("Lista os grupos de pontos e quantos pontos cada um contém.")]
    public static Task<CallToolResult> ListPointGroups() =>
        ToolInvoker.Read("list_point_groups", null, s =>
            Info.Open<PointGroup>(s.Tr, s.Civil.PointGroups).Select(g => new
            {
                handle = g.Handle.ToString(),
                name = g.Name,
                description = g.Description,
                pointCount = g.PointsCount,
                isOutOfDate = g.IsOutOfDate,
            }).ToList());
}
