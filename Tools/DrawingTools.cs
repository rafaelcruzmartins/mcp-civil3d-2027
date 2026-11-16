using System.ComponentModel;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Civil3dMcp.Infrastructure;
using Civil3dMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3dMcp.Tools;

[McpServerToolType]
public static class DrawingTools
{
    [McpServerTool(Name = "get_status", ReadOnly = true), Description(
        "Estado da conexão com o Civil 3D: versão, desenhos abertos, desenho ativo e se há comando em andamento. Use primeiro para testar a conexão.")]
    public static Task<CallToolResult> GetStatus() =>
        ToolInvoker.Raw("get_status", null, () =>
        {
            var dm = AcApp.DocumentManager;
            var active = dm.MdiActiveDocument;
            var docs = new List<object>();
            foreach (Autodesk.AutoCAD.ApplicationServices.Document d in dm)
                docs.Add(new { name = d.Name, active = d == active, readOnly = d.IsReadOnly });
            return new
            {
                product = AcApp.GetSystemVariable("PRODUCT"),
                acadVersion = AcApp.Version.ToString(),
                civil3d = CivilApplication.ActiveProduct.ToString(),
                server = McpHost.Url,
                activeDocument = active?.Name,
                commandInProgress = active?.CommandInProgress,
                isCivilDocument = active is not null && CivilDocument.GetCivilDocument(active.Database) is not null,
                documents = docs,
            };
        });

    [McpServerTool(Name = "get_drawing_info", ReadOnly = true), Description(
        "Resumo do desenho ativo: unidades, sistema de coordenadas do Civil 3D, extents, layers e contagem de objetos Civil 3D (superfícies, alinhamentos, pontos COGO, corredores, redes, sites).")]
    public static Task<CallToolResult> GetDrawingInfo() =>
        ToolInvoker.Read("get_drawing_info", null, s =>
        {
            var c = s.Civil;
            string? coord = null, zoneUnits = null;
            try
            {
                var uz = c.Settings.DrawingSettings.UnitZoneSettings;
                coord = uz.CoordinateSystemCode;
                zoneUnits = uz.DrawingUnits.ToString();
            }
            catch { }
            var layers = Info.Open<LayerTableRecord>(s.Tr, s.Get<LayerTable>(s.Db.LayerTableId)).Select(l => l.Name).ToList();
            return new
            {
                file = s.Doc.Name,
                insunits = s.Db.Insunits.ToString(),
                civilDrawingUnits = zoneUnits,
                coordinateSystem = string.IsNullOrEmpty(coord) ? null : coord,
                extMin = P3.Of(s.Db.Extmin),
                extMax = P3.Of(s.Db.Extmax),
                currentLayer = s.Get<LayerTableRecord>(s.Db.Clayer).Name,
                layers,
                counts = new
                {
                    surfaces = c.GetSurfaceIds().Count,
                    alignments = c.GetAlignmentIds().Count,
                    sites = c.GetSiteIds().Count,
                    cogoPoints = c.CogoPoints.Count,
                    pointGroups = c.PointGroups.Count,
                    corridors = c.CorridorCollection.Count,
                    pipeNetworks = c.GetPipeNetworkIds().Count,
                },
            };
        });

    [McpServerTool(Name = "list_entities", ReadOnly = true), Description(
        "Lista entidades do espaço do modelo, com filtros opcionais por tipo .NET (ex.: Polyline, TinSurface, Alignment, CogoPoint, Line) ou nome DXF, e por layer.")]
    public static Task<CallToolResult> ListEntities(
        [Description("Tipo .NET ou nome DXF, sem distinção de maiúsculas (ex.: \"Polyline\", \"AECC_TIN_SURFACE\").")] string? type = null,
        [Description("Nome do layer.")] string? layer = null,
        [Description("Máximo de itens (padrão 200).")] int limit = 200) =>
        ToolInvoker.Read("list_entities", new { type, layer, limit }, s =>
        {
            var items = new List<object>();
            var total = 0;
            foreach (ObjectId id in s.ModelSpace())
            {
                if (id.IsErased) continue;
                if (type is not null && !string.Equals(id.ObjectClass.DxfName, type, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(id.ObjectClass.Name, type, StringComparison.OrdinalIgnoreCase))
                {
                    // nome .NET só é conhecido abrindo o objeto
                    var ok = s.Tr.GetObject(id, OpenMode.ForRead, false, true) is { } probe &&
                             string.Equals(probe.GetType().Name, type, StringComparison.OrdinalIgnoreCase);
                    if (!ok) continue;
                }
                var e = (Entity)s.Tr.GetObject(id, OpenMode.ForRead, false, true);
                if (layer is not null && !string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                if (items.Count < limit) items.Add(Info.Summary(s.Tr, e));
            }
            return new { total, returned = items.Count, items };
        });

    [McpServerTool(Name = "get_entity_properties", ReadOnly = true), Description(
        "Todas as propriedades públicas simples de um objeto (AutoCAD ou Civil 3D) pelo handle, via reflexão. Útil para descobrir a API antes de usar execute_csharp.")]
    public static Task<CallToolResult> GetEntityProperties([Description("Handle hexadecimal, ex.: \"2A3F\".")] string handle) =>
        ToolInvoker.Read("get_entity_properties", new { handle }, s =>
        {
            var o = s.Tr.GetObject(s.IdFromHandle(handle), OpenMode.ForRead, false, true);
            return new { summary = Info.Summary(s.Tr, o), properties = Info.Properties(s.Tr, o) };
        });

    [McpServerTool(Name = "create_polyline"), Description(
        "Cria uma polilinha 2D (LWPOLYLINE, com Z = elevação do primeiro ponto) ou 3D. Útil como base para alinhamentos, linhas de quebra e contornos.")]
    public static Task<CallToolResult> CreatePolyline(
        [Description("Vértices, mínimo 2.")] P3[] points,
        [Description("Fechada?")] bool closed = false,
        [Description("true = Polyline3d (mantém o Z de cada vértice).")] bool is3d = false,
        [Description("Layer (criado se não existir).")] string? layer = null) =>
        ToolInvoker.Write("create_polyline", new { points, closed, is3d, layer }, s =>
        {
            if (points is null || points.Length < 2) throw new ArgumentException("Informe ao menos 2 pontos.");
            Entity ent;
            if (is3d)
            {
                var pl3 = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(points.Select(p => p.ToPoint()).ToArray()), closed);
                ent = pl3;
            }
            else
            {
                var pl = new Polyline();
                for (var i = 0; i < points.Length; i++) pl.AddVertexAt(i, points[i].To2d(), 0, 0, 0);
                pl.Elevation = points[0].Z;
                pl.Closed = closed;
                ent = pl;
            }
            s.AddToCurrentSpace(ent, layer);
            return Info.Summary(s.Tr, ent);
        });

    [McpServerTool(Name = "delete_entities", Destructive = true), Description(
        "Apaga objetos pelos handles (atômico: ou apaga todos ou nenhum). Desfazível com U.")]
    public static Task<CallToolResult> DeleteEntities([Description("Handles hexadecimais.")] string[] handles) =>
        ToolInvoker.Write("delete_entities", new { handles }, s =>
        {
            var ids = handles.Select(s.IdFromHandle).ToList();
            foreach (var id in ids) s.Tr.GetObject(id, OpenMode.ForWrite).Erase();
            return new { deleted = ids.Count };
        });

    [McpServerTool(Name = "zoom_extents"), Description("Dá zoom extents na vista ativa.")]
    public static Task<CallToolResult> ZoomExtents() =>
        ToolInvoker.Raw("zoom_extents", null, () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("Nenhum desenho aberto.");
            doc.SendStringToExecute("._ZOOM _E ", true, false, false);
            return new { queued = true };
        });

    [McpServerTool(Name = "send_command"), Description(
        "Envia um comando para a linha de comando do Civil 3D (assíncrono: retorna antes do comando terminar e não devolve a saída). " +
        "Use espaços ou \\n como Enter, ex.: \"._REGEN \". Prefira as ferramentas tipadas ou execute_csharp quando possível.")]
    public static Task<CallToolResult> SendCommand([Description("Texto do comando, com espaço/Enter final.")] string command) =>
        ToolInvoker.Raw("send_command", new { command }, () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("Nenhum desenho aberto.");
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Comando vazio.");
            var text = command.Replace("\\n", "\n");
            if (!text.EndsWith(' ') && !text.EndsWith('\n')) text += " ";
            doc.SendStringToExecute(text, true, false, true);
            return new { queued = text };
        });
}
