using System.Net;
using Civil3dMcp.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Civil3dMcp.Server;

/// <summary>Servidor MCP (Streamable HTTP, stateless) em Kestrel, só em loopback.</summary>
public static class McpHost
{
    private static WebApplication? _app;

    public static int Port { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("CIVIL3D_MCP_PORT"), out var p) ? p : 5101;

    public static string Url => $"http://127.0.0.1:{Port}/mcp";
    public static bool Running => _app is not null;
    public static string? LastError { get; private set; }

    public static async Task StartAsync()
    {
        if (_app is not null) return;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetDirectoryName(typeof(McpHost).Assembly.Location),
                EnvironmentName = "Production",
                Args = [],
            });
            builder.Configuration.Sources.Clear();

            // Nada de handlers de console (Ctrl+C) nem logging em console dentro do AutoCAD.
            builder.Services.AddSingleton<IHostLifetime, NoopLifetime>();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new FileLoggerProvider());

            builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, Port));

            builder.Services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new() { Name = "civil3d", Version = "1.0.0" };
                    o.ServerInstructions =
                        "Ferramentas para o Autodesk Civil 3D 2027 aberto nesta máquina. Ângulos em graus, coordenadas no WCS " +
                        "e unidades do desenho. Cada chamada de escrita é um grupo de undo (U desfaz). Para o que não houver " +
                        "ferramenta tipada, use execute_csharp com a API .NET do Civil 3D (comece com commit=false).";
                })
                .WithHttpTransport(o => o.Stateless = true)
                .WithToolsFromAssembly(typeof(McpHost).Assembly, Fmt.Options);

            var app = builder.Build();
            app.Use(LoopbackGuard);
            app.MapGet("/", () => Results.Text($"Civil3dMcp ativo. Endpoint MCP: {Url}"));
            app.MapMcp("/mcp");

            await app.StartAsync();
            _app = app;
            LastError = null;
            Log.Info($"Servidor MCP ouvindo em {Url}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("Falha ao iniciar o servidor MCP", ex);
        }
    }

    public static void Stop()
    {
        var app = _app;
        _app = null;
        if (app is null) return;
        try
        {
            app.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3));
            ((IDisposable)app).Dispose();
        }
        catch (Exception ex) { Log.Warn("Erro ao parar o servidor: " + ex.Message); }
    }

    /// <summary>
    /// 403 se Host ou Origin não forem locais: protege contra DNS rebinding e CSRF
    /// (execute_csharp é execução de código).
    /// </summary>
    private static async Task LoopbackGuard(HttpContext ctx, Func<Task> next)
    {
        var host = ctx.Request.Host.Host;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!IsLocal(host) || (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && IsLocal(o.Host))))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Somente acesso local.");
            return;
        }
        await next();
    }

    private static bool IsLocal(string host) =>
        host is "127.0.0.1" or "localhost" or "[::1]" or "::1";

    private sealed class NoopLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
