using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3dMcp.Infrastructure;

/// <summary>
/// Despacha trabalho para a thread principal do AutoCAD. Se a thread não pegar o trabalho dentro do
/// timeout, ele é cancelado e nunca roda atrasado.
/// </summary>
public static class MainThread
{
    private static SynchronizationContext? _ctx;

    public static bool IsReady => _ctx is not null;

    /// <summary>Chamado no primeiro Application.Idle (no Initialize o contexto pode ser nulo).</summary>
    public static void Capture()
    {
        _ctx ??= SynchronizationContext.Current;
        if (_ctx is null) Log.Warn("SynchronizationContext nulo no Idle");
    }

    public static Task<T> InvokeAsync<T>(Func<T> work, int timeoutMs)
    {
        var ctx = _ctx ?? throw new InvalidOperationException(
            "O plugin ainda não está pronto (aguardando o primeiro ciclo ocioso do Civil 3D).");

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = 0; // 0 = pendente, 1 = rodando, 2 = cancelado

        ctx.Post(_ =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, null);

        _ = Task.Delay(timeoutMs).ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                tcs.TrySetException(new TimeoutException(
                    $"O Civil 3D não atendeu em {timeoutMs / 1000.0:0}s. Ele pode estar ocupado, com um comando ativo ou com uma caixa de diálogo aberta."));
        }, TaskScheduler.Default);

        return tcs.Task;
    }

    /// <summary>Roda em contexto de comando (necessário para Editor.CommandAsync).</summary>
    public static Task<T> InvokeInCommandContextAsync<T>(Func<Task<T>> work, int timeoutMs) =>
        InvokeAsync(() =>
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dm = AcApp.DocumentManager;
            _ = dm.ExecuteInCommandContextAsync(async _ =>
            {
                try { tcs.TrySetResult(await work()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, null);
            return tcs.Task;
        }, timeoutMs).Unwrap();
}
