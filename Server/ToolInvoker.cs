using System.Diagnostics;
using Autodesk.AutoCAD.Runtime;
using Civil3dMcp.Infrastructure;
using ModelContextProtocol.Protocol;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3dMcp.Server;

/// <summary>Erro de ferramenta com dados estruturados para o modelo.</summary>
public sealed class ToolException(string message, object? details = null) : System.Exception(message)
{
    public object? Details { get; } = details;
}

/// <summary>
/// Ponto único de execução das ferramentas: log, tempo, despacho para a thread do AutoCAD e conversão
/// de resultado/erro em JSON {ok, tool, durationMs, units, result}.
/// </summary>
public static class ToolInvoker
{
    public const int DefaultTimeoutMs = 60_000;

    /// <summary>Consulta: lock de leitura, transação sempre abortada.</summary>
    public static Task<CallToolResult> Read(string tool, object? args, Func<DocScope, object?> body, int timeoutMs = DefaultTimeoutMs) =>
        Run(tool, args, () => MainThread.InvokeAsync(() =>
        {
            using var s = DocScope.Open(false, tool);
            return (body(s), Units(s));
        }, timeoutMs));

    /// <summary>Escrita: lock de escrita (um grupo de undo), commit no sucesso.</summary>
    public static Task<CallToolResult> Write(string tool, object? args, Func<DocScope, object?> body, int timeoutMs = DefaultTimeoutMs) =>
        Run(tool, args, () => MainThread.InvokeAsync(() =>
        {
            using var s = DocScope.Open(true, tool);
            var r = body(s);
            s.Commit();
            return (r, Units(s));
        }, timeoutMs));

    /// <summary>Escrita controlada: commit só se <paramref name="commit"/> for true.</summary>
    public static Task<CallToolResult> WriteOptional(string tool, object? args, bool commit, Func<DocScope, object?> body, int timeoutMs = DefaultTimeoutMs) =>
        Run(tool, args, () => MainThread.InvokeAsync(() =>
        {
            using var s = DocScope.Open(true, tool);
            var r = body(s);
            if (commit) s.Commit();
            return (r, Units(s));
        }, timeoutMs));

    /// <summary>Roda na thread principal sem lock/transação (o corpo cuida disso).</summary>
    public static Task<CallToolResult> Raw(string tool, object? args, Func<object?> body, int timeoutMs = DefaultTimeoutMs) =>
        Run(tool, args, () => MainThread.InvokeAsync(() => (body(), (string?)null), timeoutMs));

    /// <summary>Corpo assíncrono livre (ex.: compilar fora da thread principal antes de executar).</summary>
    public static Task<CallToolResult> Custom(string tool, object? args, Func<Task<object?>> body) =>
        Run(tool, args, async () => (await body(), (string?)null));

    private static async Task<CallToolResult> Run(string tool, object? args, Func<Task<(object? result, string? units)>> exec)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (result, units) = await exec();
            sw.Stop();
            Log.Info($"{tool} ok {sw.ElapsedMilliseconds}ms");
            return Ok(new { ok = true, tool, durationMs = sw.ElapsedMilliseconds, units, result });
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            var e = ex is AggregateException { InnerException: not null } ag ? ag.InnerException! : ex;
            var validation = e is ArgumentException or ToolException or InvalidOperationException or TimeoutException;
            Log.Warn($"{tool} erro {sw.ElapsedMilliseconds}ms: {e.GetType().Name}: {e.Message} args={SafeArgs(args)}");
            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = Fmt.Out(new
                        {
                            ok = false,
                            tool,
                            durationMs = sw.ElapsedMilliseconds,
                            error = new
                            {
                                type = e.GetType().Name,
                                message = e.Message,
                                errorStatus = (e as Autodesk.AutoCAD.Runtime.Exception)?.ErrorStatus.ToString(),
                                details = (e as ToolException)?.Details,
                                stackTrace = validation ? null : e.StackTrace,
                            },
                        }),
                    },
                ],
            };
        }
    }

    private static CallToolResult Ok(object payload) => new()
    {
        Content = [new TextContentBlock { Text = Fmt.Out(payload) }],
    };

    private static string? Units(DocScope s)
    {
        try { return s.Db.Insunits.ToString(); }
        catch { return null; }
    }

    private static string SafeArgs(object? args)
    {
        try
        {
            var t = Fmt.Out(args);
            return t.Length > 500 ? t[..500] + "…" : t;
        }
        catch { return "?"; }
    }
}
