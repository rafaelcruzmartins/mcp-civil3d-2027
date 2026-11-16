using System.Reflection;
using System.Runtime.Loader;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(Civil3dMcp.Loader.LoaderPlugin))]
[assembly: CommandClass(typeof(Civil3dMcp.Loader.LoaderCommands))]

namespace Civil3dMcp.Loader;

/// <summary>
/// Carrega o Civil3dMcp.dll e o SDK MCP num AssemblyLoadContext isolado. O Civil 3D 2027 traz o servidor MCP
/// da Autodesk com ModelContextProtocol 0.4 no contexto padrão; sem isolamento o nosso 2.2 não carrega.
/// Tudo que não estiver na pasta do bundle (AutoCAD, Civil 3D, ASP.NET Core) vem do contexto padrão.
/// </summary>
public sealed class LoaderPlugin : IExtensionApplication
{
    internal static Type? Entry;

    public void Initialize() => AcApp.Idle += OnFirstIdle;

    private static void OnFirstIdle(object? sender, EventArgs e)
    {
        AcApp.Idle -= OnFirstIdle;
        try
        {
            var dir = Path.GetDirectoryName(typeof(LoaderPlugin).Assembly.Location)!;
            var alc = new PluginContext(dir);
            var asm = alc.LoadFromAssemblyPath(Path.Combine(dir, "Civil3dMcp.dll"));
            Entry = asm.GetType("Civil3dMcp.Entry", true)!;
            Invoke("Start");
        }
        catch (System.Exception ex)
        {
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nCivil3dMcp: falha ao carregar: {ex.Message}\n");
        }
    }

    internal static string? Invoke(string method) =>
        Entry?.GetMethod(method, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as string;

    public void Terminate()
    {
        try { Invoke("Stop"); } catch { }
    }

    private sealed class PluginContext(string dir) : AssemblyLoadContext("Civil3dMcp", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) && name.Name != "Civil3dMcp.Loader" ? LoadFromAssemblyPath(path) : null;
        }
    }
}

public static class LoaderCommands
{
    [CommandMethod("C3DMCP", CommandFlags.Session)]
    public static void Status() => Write(LoaderPlugin.Invoke("Status") ?? "Civil3dMcp não carregado.");

    [CommandMethod("C3DMCPRESTART", CommandFlags.Session)]
    public static void Restart() => Write(LoaderPlugin.Invoke("Restart") ?? "Civil3dMcp não carregado.");

    private static void Write(string msg) =>
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n{msg}\n");
}
