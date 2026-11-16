using System.ComponentModel;
using Civil3dMcp.Infrastructure;
using Civil3dMcp.Scripting;
using Civil3dMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Civil3dMcp.Tools;

[McpServerToolType]
public static class ScriptTools
{
    [McpServerTool(Name = "execute_csharp", Destructive = true), Description(
        "Compila e executa código C# dentro do Civil 3D (API .NET do AutoCAD e do Civil 3D), na thread principal, com o documento travado e uma transação aberta. " +
        "O código é o CORPO de um método que retorna object: use 'return valor;' para devolver um resultado (serializado em JSON) e Print(x) para saída de texto. " +
        "Globais: Doc, Db, Ed, Tr (Transaction), Civil (CivilDocument), Print(obj), Id(handle) -> ObjectId, Open<T>(id|handle, mode), " +
        "ModelSpace(mode), CurrentSpace(mode), Add(entity, layer) -> ObjectId, Rad(graus), Deg(rad). " +
        "Usings já incluídos: System, System.Linq, System.Collections.Generic, System.Text, Autodesk.AutoCAD.{ApplicationServices,DatabaseServices,Geometry,EditorInput}, " +
        "Autodesk.Civil, Autodesk.Civil.{ApplicationServices,DatabaseServices,DatabaseServices.Styles}. " +
        "commit=false (padrão) aborta a transação: nada é gravado; use commit=true para persistir (vira um grupo de undo). " +
        "Não use await em código que dependa da thread do AutoCAD (deadlock). Ex.: 'return Civil.GetSurfaceIds().Count;'")]
    public static Task<CallToolResult> ExecuteCSharp(
        [Description("Corpo do método C#.")] string code,
        [Description("true = grava as alterações; false (padrão) = desfaz tudo ao final (teste seguro).")] bool commit = false,
        [Description("Usings adicionais, ex.: [\"Autodesk.Civil.DatabaseServices.Styles\", \"System.IO\"].")] string[]? usings = null,
        [Description("Timeout em segundos (padrão 120).")] int timeoutSeconds = 120) =>
        ToolInvoker.Custom("execute_csharp", new { codeLength = code?.Length, commit }, async () =>
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Código vazio.");
            var type = await Task.Run(() => CSharpRunner.Compile(code, usings ?? []));
            return await MainThread.InvokeAsync<object?>(() =>
            {
                using var s = DocScope.Open(true, "execute_csharp");
                var (result, output) = CSharpRunner.Execute(type, s);
                if (commit) s.Commit();
                object? safe;
                try { Fmt.Out(result); safe = result; }
                catch { safe = result?.ToString(); }
                return new { committed = commit, output = output.Length > 0 ? output : null, result = safe };
            }, Math.Max(5, timeoutSeconds) * 1000);
        });
}
