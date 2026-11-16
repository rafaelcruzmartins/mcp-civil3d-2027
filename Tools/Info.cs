using System.Collections;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices.Styles;
using Civil3dMcp.Infrastructure;

namespace Civil3dMcp.Tools;

/// <summary>Resumo e detalhes de objetos (AutoCAD e Civil 3D).</summary>
internal static class Info
{
    public static string H(ObjectId id) => id.IsNull ? "" : id.Handle.ToString();

    public static string? NameOf(DBObject o) => o switch
    {
        Autodesk.Civil.DatabaseServices.Entity ce => ce.Name,
        SymbolTableRecord str => str.Name,
        _ => TryProp(o, "Name") as string,
    };

    public static object Summary(Transaction tr, DBObject o)
    {
        var ent = o as Entity;
        return new
        {
            handle = o.Handle.ToString(),
            type = o.GetType().Name,
            dxf = o.GetRXClass().DxfName,
            name = NameOf(o),
            layer = ent?.Layer,
            style = StyleName(tr, o),
        };
    }

    public static string? StyleName(Transaction tr, DBObject o)
    {
        try
        {
            if (TryProp(o, "StyleId") is ObjectId sid && !sid.IsNull &&
                tr.GetObject(sid, OpenMode.ForRead, false, true) is StyleBase sb)
                return sb.Name;
        }
        catch { }
        return null;
    }

    public static ObjectId StyleId(Transaction tr, IEnumerable<ObjectId> styles, string? name, string kind)
    {
        ObjectId first = ObjectId.Null;
        var names = new List<string>();
        foreach (var id in styles)
        {
            if (first.IsNull) first = id;
            var n = (tr.GetObject(id, OpenMode.ForRead) as StyleBase)?.Name ?? "";
            names.Add(n);
            if (name is not null && string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return id;
        }
        if (name is not null)
            throw new Server.ToolException($"Estilo '{name}' não encontrado em {kind}.", new { available = names });
        if (first.IsNull) throw new InvalidOperationException($"O desenho não tem nenhum estilo em {kind}.");
        return first;
    }

    public static object? TryProp(object o, string name)
    {
        try { return o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); }
        catch { return null; }
    }

    /// <summary>Propriedades públicas legíveis de tipo simples, via reflexão (cada uma isolada em try).</summary>
    public static Dictionary<string, object?> Properties(Transaction tr, DBObject o, int maxCollection = 50)
    {
        var d = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            if (p.Name is "UnmanagedObject" or "AcadObject" or "ClassID" or "Drawable" or "Bounds" or "ExtensionDictionary" or "ObjectBirthVersion") continue;
            try
            {
                var v = p.GetValue(o);
                var s = Simple(tr, v, maxCollection);
                if (s is not Skip) d[p.Name] = s;
            }
            catch { /* propriedade não aplicável a este objeto */ }
        }
        return new Dictionary<string, object?>(d);
    }

    /// <summary>Propriedades numéricas/texto de um objeto qualquer (ex.: structs de propriedades de superfície).</summary>
    public static Dictionary<string, object?> PlainProperties(object o)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            try
            {
                d[p.Name] = p.GetValue(o) switch
                {
                    double v => Fmt.R(v, 6),
                    Enum e => e.ToString(),
                    string or bool or int or long or uint or ulong => p.GetValue(o),
                    _ => null,
                };
            }
            catch { }
        }
        return d;
    }

    private sealed class Skip { public static readonly Skip I = new(); }

    private static object? Simple(Transaction tr, object? v, int maxCollection) => v switch
    {
        null => null,
        string or bool or int or long or short or byte or uint or ulong or ushort or decimal => v,
        double dbl => Fmt.R(dbl, 6),
        float f => Fmt.R(f, 6),
        Enum e => e.ToString(),
        Point3d p => P3.Of(p),
        Point2d p => P3.Of(p),
        Vector3d vec => new { x = Fmt.R(vec.X, 6), y = Fmt.R(vec.Y, 6), z = Fmt.R(vec.Z, 6) },
        ObjectId id => id.IsNull ? null : new { handle = H(id), name = SafeName(tr, id) },
        Autodesk.AutoCAD.Colors.Color c => c.ColorNameForDisplay,
        Handle h => h.ToString(),
        DateTime dt => dt.ToString("s"),
        ObjectIdCollection ids => ids.Cast<ObjectId>().Take(maxCollection).Select(H).ToList(),
        _ => Skip.I,
    };

    private static string? SafeName(Transaction tr, ObjectId id)
    {
        try { return tr.GetObject(id, OpenMode.ForRead, false, true) is { } o ? NameOf(o) : null; }
        catch { return null; }
    }

    public static IEnumerable<T> Open<T>(Transaction tr, IEnumerable ids) where T : DBObject
    {
        foreach (ObjectId id in ids)
            if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead, false, true) is T t) yield return t;
    }

    public static T ByName<T>(Transaction tr, IEnumerable ids, string name, string kind) where T : Autodesk.Civil.DatabaseServices.Entity
    {
        var all = Open<T>(tr, ids).ToList();
        return all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new Server.ToolException($"{kind} '{name}' não encontrado(a).", new { available = all.Select(x => x.Name).ToList() });
    }
}
