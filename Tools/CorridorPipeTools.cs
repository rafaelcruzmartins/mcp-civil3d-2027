using System.ComponentModel;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Civil3dMcp.Infrastructure;
using Civil3dMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Civil3dMcp.Tools;

[McpServerToolType]
public static class CorridorPipeTools
{
    private static object DescribeCorridor(DocScope s, Corridor c) => new
    {
        handle = c.Handle.ToString(),
        name = c.Name,
        description = c.Description,
        isOutOfDate = c.IsOutOfDate,
        style = Info.StyleName(s.Tr, c),
        baselines = c.Baselines.Select(b => new
        {
            name = b.Name,
            alignment = b.AlignmentId.IsNull ? null : s.Get<Alignment>(b.AlignmentId).Name,
            profile = b.ProfileId.IsNull ? null : s.Get<Profile>(b.ProfileId).Name,
            startStation = Fmt.R(b.StartStation),
            endStation = Fmt.R(b.EndStation),
            regions = b.BaselineRegions.Count,
        }).ToList(),
        codes = new { point = c.GetPointCodes(), link = c.GetLinkCodes(), shape = c.GetShapeCodes() },
    };

    [McpServerTool(Name = "list_corridors", ReadOnly = true), Description(
        "Lista os corredores com baselines (alinhamento/perfil, estacas, nº de regiões), códigos de ponto/link/shape e se estão desatualizados.")]
    public static Task<CallToolResult> ListCorridors() =>
        ToolInvoker.Read("list_corridors", null, s =>
            Info.Open<Corridor>(s.Tr, s.Civil.CorridorCollection).Select(c => DescribeCorridor(s, c)).ToList());

    [McpServerTool(Name = "rebuild_corridor"), Description("Reconstrói um corredor (ou todos, se 'name' for omitido).")]
    public static Task<CallToolResult> RebuildCorridor([Description("Nome do corredor; vazio = todos.")] string? name = null) =>
        ToolInvoker.Write("rebuild_corridor", new { name }, s =>
        {
            var all = Info.Open<Corridor>(s.Tr, s.Civil.CorridorCollection).ToList();
            var targets = name is null ? all
                : [all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolException($"Corredor '{name}' não encontrado.", new { available = all.Select(c => c.Name).ToList() })];
            foreach (var c in targets) { c.UpgradeOpen(); c.Rebuild(); }
            return new { rebuilt = targets.Select(c => c.Name).ToList() };
        }, 600_000);

    [McpServerTool(Name = "list_pipe_networks", ReadOnly = true), Description(
        "Lista as redes de tubulação por gravidade com tubos (diâmetro interno, comprimento 2D, declividade, estruturas de início/fim) e estruturas (posição, cota de tampa e de fundo).")]
    public static Task<CallToolResult> ListPipeNetworks(
        [Description("Nome da rede; vazio = todas.")] string? name = null,
        [Description("Incluir a lista de tubos e estruturas (padrão true).")] bool details = true) =>
        ToolInvoker.Read("list_pipe_networks", new { name, details }, s =>
        {
            var nets = Info.Open<Network>(s.Tr, s.Civil.GetPipeNetworkIds())
                .Where(n => name is null || string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return nets.Select(n =>
            {
                var pipeIds = n.GetPipeIds();
                var structIds = n.GetStructureIds();
                return new
                {
                    handle = n.Handle.ToString(),
                    name = n.Name,
                    pipeCount = pipeIds.Count,
                    structureCount = structIds.Count,
                    pipes = !details ? null : Info.Open<Pipe>(s.Tr, pipeIds).Select(p => new
                    {
                        handle = p.Handle.ToString(),
                        name = p.Name,
                        innerDiameter = Fmt.R(p.InnerDiameterOrWidth),
                        length2d = Fmt.R(p.Length2D),
                        slope = Fmt.R(p.Slope, 6),
                        start = P3.Of(p.StartPoint),
                        end = P3.Of(p.EndPoint),
                        startStructure = p.StartStructureId.IsNull ? null : s.Get<Structure>(p.StartStructureId).Name,
                        endStructure = p.EndStructureId.IsNull ? null : s.Get<Structure>(p.EndStructureId).Name,
                        partSize = p.PartSizeName,
                    }).Cast<object>().ToList(),
                    structures = !details ? null : Info.Open<Structure>(s.Tr, structIds).Select(st => new
                    {
                        handle = st.Handle.ToString(),
                        name = st.Name,
                        position = P3.Of(st.Position),
                        rimElevation = Fmt.R(st.RimElevation),
                        sumpElevation = Fmt.R(st.SumpElevation),
                        partSize = st.PartSizeName,
                    }).Cast<object>().ToList(),
                };
            }).ToList();
        });
}
