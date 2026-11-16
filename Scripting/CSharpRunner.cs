using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using Civil3dMcp.Infrastructure;
using Civil3dMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Civil3dMcp.Scripting;

/// <summary>
/// Globais disponíveis no código do execute_csharp (o script é o corpo de um método de uma classe
/// derivada desta, então tudo aqui é acessível sem prefixo).
/// </summary>
public abstract class ScriptGlobals
{
    private DocScope _s = null!;
    private StringBuilder _out = null!;

    public Document Doc => _s.Doc;
    public Database Db => _s.Db;
    public Editor Ed => _s.Ed;
    public Transaction Tr => _s.Tr;
    public CivilDocument Civil => _s.Civil;

    /// <summary>Escreve uma linha na saída devolvida ao Claude.</summary>
    public void Print(object? value) => _out.AppendLine(value switch
    {
        null => "null",
        string str => str,
        _ when value.GetType().IsPrimitive || value is decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => SafeJson(value),
    });

    public ObjectId Id(string handle) => _s.IdFromHandle(handle);
    public T Open<T>(ObjectId id, OpenMode mode = OpenMode.ForRead) where T : DBObject => _s.Get<T>(id, mode);
    public T Open<T>(string handle, OpenMode mode = OpenMode.ForRead) where T : DBObject => _s.Get<T>(_s.IdFromHandle(handle), mode);
    public BlockTableRecord ModelSpace(OpenMode mode = OpenMode.ForRead) => _s.ModelSpace(mode);
    public BlockTableRecord CurrentSpace(OpenMode mode = OpenMode.ForRead) => _s.CurrentSpace(mode);
    public ObjectId Add(Entity e, string? layer = null) => _s.AddToCurrentSpace(e, layer);
    public static double Rad(double deg) => deg * Math.PI / 180.0;
    public static double Deg(double rad) => rad * 180.0 / Math.PI;

    internal void Init(DocScope s, StringBuilder output) { _s = s; _out = output; }
    public abstract object? __Run();

    private static string SafeJson(object v)
    {
        try { return Fmt.Out(v); } catch { return v.ToString() ?? ""; }
    }
}

public static class CSharpRunner
{
    private const string FileName = "execute_csharp.csx";
    private static readonly ConcurrentDictionary<string, Type> Cache = new();
    private static List<MetadataReference>? _refs;
    private static readonly object RefsGate = new();

    private static readonly string[] DefaultUsings =
    [
        "System", "System.Linq", "System.Collections.Generic", "System.Text",
        "Autodesk.AutoCAD.ApplicationServices", "Autodesk.AutoCAD.DatabaseServices",
        "Autodesk.AutoCAD.Geometry", "Autodesk.AutoCAD.EditorInput",
        "Autodesk.Civil", "Autodesk.Civil.ApplicationServices", "Autodesk.Civil.DatabaseServices",
        "Autodesk.Civil.DatabaseServices.Styles",
    ];

    /// <summary>Aquece o Roslyn em background para a primeira chamada não demorar.</summary>
    public static void WarmUp()
    {
        try { Compile("return 1;", []); }
        catch (Exception ex) { Log.Warn("WarmUp do Roslyn falhou: " + ex.Message); }
    }

    public static Type Compile(string code, IReadOnlyList<string> extraUsings)
    {
        var usings = DefaultUsings.Concat(extraUsings.Select(u => u.Trim().TrimEnd(';').Replace("using ", "")))
            .Where(u => u.Length > 0).Distinct().ToList();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", usings) + "\n" + code)));
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var cls = "__Script_" + key[..16];
        var src = new StringBuilder();
        foreach (var u in usings) src.Append("using ").Append(u).AppendLine(";");
        src.AppendLine($"public sealed class {cls} : Civil3dMcp.Scripting.ScriptGlobals {{");
        src.AppendLine("public override object __Run() {");
        src.AppendLine($"#line 1 \"{FileName}\"");
        src.AppendLine(code);
        src.AppendLine("#line default");
        src.AppendLine("#pragma warning disable CS0162");
        src.AppendLine("return null; } }");

        var tree = CSharpSyntaxTree.ParseText(src.ToString(),
            new CSharpParseOptions(LanguageVersion.Latest), path: FileName, encoding: Encoding.UTF8);
        var comp = CSharpCompilation.Create("c3dscript_" + key[..16], [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug, nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true, platform: Platform.X64));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var res = comp.Emit(pe, pdb, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
            debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
        if (!res.Success)
        {
            var errors = res.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d =>
                {
                    var span = d.Location.GetMappedLineSpan();
                    return new { line = span.StartLinePosition.Line + 1, column = span.StartLinePosition.Character + 1, code = d.Id, message = d.GetMessage() };
                }).Take(20).ToList();
            throw new ToolException("Erro de compilação no código C#.", errors);
        }

        // Mesmo contexto do plugin (isolado), para ScriptGlobals ser o mesmo tipo.
        pe.Position = 0; pdb.Position = 0;
        var asm = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(ScriptGlobals).Assembly)!.LoadFromStream(pe, pdb);
        var type = asm.GetType(cls)!;
        Cache[key] = type;
        return type;
    }

    public static (object? result, string output) Execute(Type type, DocScope scope)
    {
        var g = (ScriptGlobals)Activator.CreateInstance(type)!;
        var output = new StringBuilder();
        g.Init(scope, output);
        var result = g.__Run();
        return (result, output.ToString());
    }

    private static List<MetadataReference> References()
    {
        lock (RefsGate)
        {
            if (_refs is not null) return _refs;
            var list = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                string loc;
                try { loc = a.Location; } catch { continue; }
                if (string.IsNullOrEmpty(loc) || !File.Exists(loc) || !seen.Add(Path.GetFileName(loc))) continue;
                try { list.Add(MetadataReference.CreateFromFile(loc)); } catch { /* assembly nativo/misto ilegível */ }
            }
            // Garante as do Civil 3D mesmo se ainda não carregadas.
            var acad = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            foreach (var rel in new[] { @"C3D\AeccDbMgd.dll", @"ACA\AecBaseMgd.dll", "acdbmgd.dll", "acmgd.dll", "accoremgd.dll" })
            {
                var p = Path.Combine(acad, rel);
                if (File.Exists(p) && seen.Add(Path.GetFileName(p))) list.Add(MetadataReference.CreateFromFile(p));
            }
            _refs = list;
            return list;
        }
    }
}
