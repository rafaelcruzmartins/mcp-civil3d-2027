using System.ComponentModel;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using Civil3dMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Civil3dMcp.Tools;

[McpServerToolType]
public static class StyleTools
{
    private static readonly string[] Kinds =
    [
        "surface", "alignment", "profile", "profileview", "profileviewbandset", "point", "pointlabel",
        "pipe", "structure", "corridor", "assembly", "samplelineview", "sectionview",
        "alignmentlabelset", "profilelabelset", "marker", "label",
    ];

    [McpServerTool(Name = "list_styles", ReadOnly = true), Description(
        "Lista nomes de estilos do Civil 3D por tipo: surface, alignment, profile, profileview, profileviewbandset, point, pointlabel, pipe, structure, corridor, assembly, sectionview, alignmentlabelset, profilelabelset, marker. Também lista os sites com kind=site.")]
    public static Task<CallToolResult> ListStyles([Description("Tipo de estilo.")] string kind) =>
        ToolInvoker.Read("list_styles", new { kind }, s =>
        {
            var st = s.Civil.Styles;
            IEnumerable<ObjectId> ids = kind.Trim().ToLowerInvariant() switch
            {
                "surface" => st.SurfaceStyles,
                "alignment" => st.AlignmentStyles,
                "profile" => st.ProfileStyles,
                "profileview" => st.ProfileViewStyles,
                "profileviewbandset" => st.ProfileViewBandSetStyles,
                "point" => st.PointStyles,
                "pointlabel" => st.LabelStyles.PointLabelStyles.LabelStyles,
                "pipe" => st.PipeStyles,
                "structure" => st.StructureStyles,
                "corridor" => st.CorridorStyles,
                "assembly" => st.AssemblyStyles,
                "sectionview" => st.SectionViewStyles,
                "samplelineview" => st.SampleLineStyles,
                "alignmentlabelset" => st.LabelSetStyles.AlignmentLabelSetStyles,
                "profilelabelset" => st.LabelSetStyles.ProfileLabelSetStyles,
                "marker" => st.MarkerStyles,
                "site" => s.Civil.GetSiteIds().Cast<ObjectId>(),
                _ => throw new ToolException($"Tipo '{kind}' desconhecido.", new { kinds = Kinds.Append("site") }),
            };
            return Info.Open<Autodesk.AutoCAD.DatabaseServices.DBObject>(s.Tr, ids.ToList()).Select(o => new
            {
                handle = o.Handle.ToString(),
                name = o is StyleBase sb ? sb.Name : (o as Site)?.Name,
            }).ToList();
        });
}
