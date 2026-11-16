using Civil3dMcp.Infrastructure;
using Civil3dMcp.Scripting;
using Civil3dMcp.Server;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3dMcp;

/// <summary>
/// Ponto de entrada chamado por reflexão pelo Civil3dMcp.Loader (que roda no contexto padrão e carrega
/// este assembly num AssemblyLoadContext isolado). Start é chamado na thread principal, no primeiro Idle.
/// </summary>
public static class Entry
{
    public static string Start()
    {
        Log.Info($"Carregando Civil3dMcp {typeof(Entry).Assembly.GetName().Version}");
        MainThread.Capture();
        _ = Task.Run(McpHost.StartAsync);
        _ = Task.Run(CSharpRunner.WarmUp);
        try
        {
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                $"\nCivil3dMcp: servidor MCP em {McpHost.Url} (comando C3DMCP para status).\n");
        }
        catch { /* sem documento */ }
        return "ok";
    }

    public static string Stop()
    {
        McpHost.Stop();
        return "parado";
    }

    public static string Status() => McpHost.Running
        ? $"Civil3dMcp ativo em {McpHost.Url}. Logs: {Log.Dir}"
        : $"Civil3dMcp parado. Último erro: {McpHost.LastError ?? "(nenhum)"}. Use C3DMCPRESTART.";

    public static string Restart()
    {
        McpHost.Stop();
        _ = Task.Run(McpHost.StartAsync);
        return $"Reiniciando Civil3dMcp em {McpHost.Url}...";
    }
}
