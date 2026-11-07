using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PivotPic.Services;

/// <summary>单实例互斥与唤起：命名 Mutex 判重，命名管道把二次启动请求转给已运行实例。</summary>
public sealed class SingleInstanceService : ISingleInstanceService
{
    /// <summary>Local\ 前缀限定当前登录会话，多用户同时运行互不干扰。</summary>
    private const string MutexName = @"Local\PivotPic.SingleInstance.7F3A9C2E";

    /// <summary>唤起管道名。</summary>
    private const string PipeName = "PivotPic.Activate.7F3A9C2E";

    /// <summary>唤起指令。</summary>
    private const string ActivateCommand = "ACTIVATE";

    /// <summary>连接已有实例的超时时间。</summary>
    private const int ConnectTimeoutMs = 300;

    private readonly object _gate = new();

    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _listenerSource;
    private Task? _listenerTask;
    private bool _disposed;

    /// <inheritdoc />
    public event Action? ActivationRequested;

    /// <summary>尝试成为唯一实例；失败时唤醒已有实例并返回 false。</summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            // 幂等：重复调用直接返回首次结果，避免重复建管道。
            if (_mutex is not null)
            {
                return _ownsMutex;
            }

            try
            {
                var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

                if (!createdNew)
                {
                    // 已有实例在跑：它才持有 mutex，这里不能 ReleaseMutex。
                    mutex.Dispose();
                    SignalExistingInstance();
                    return false;
                }

                _mutex = mutex;
                _ownsMutex = true;
                StartListener();
                return true;
            }
            catch (Exception ex)
            {
                // 权限不足或被其它会话占用时按「拿不到单实例」处理，但也不阻断启动。
                AppServices.Log?.Warn("单实例", $"创建互斥量失败：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>释放互斥量并停止管道监听。</summary>
    public void Dispose()
    {
        CancellationTokenSource? source;
        Task? listener;
        Mutex? mutex;
        var owned = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            source = _listenerSource;
            listener = _listenerTask;
            mutex = _mutex;
            owned = _ownsMutex;
            _listenerSource = null;
            _listenerTask = null;
            _mutex = null;
            _ownsMutex = false;
        }

        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 监听线程已自行结束并释放，忽略。
        }

        // 不等待 listener 结束：它可能正阻塞在 WaitForConnectionAsync，取消后自行退出。
        _ = listener;

        if (mutex is not null)
        {
            if (owned)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (Exception)
                {
                    // 未持有或已被释放时 ReleaseMutex 会抛，忽略即可。
                }
            }

            try
            {
                mutex.Dispose();
            }
            catch (Exception)
            {
                // 释放失败无补救手段。
            }
        }

        source?.Dispose();
    }

    /// <summary>向已运行的实例发送唤起指令；连不上就安静放弃（对方可能正在退出）。</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.Out,
                options: PipeOptions.Asynchronous);

            client.Connect(ConnectTimeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(ActivateCommand);
        }
        catch (Exception ex)
        {
            // 超时/管道不存在/对方已退出——都不影响「本次启动退出」的结论。
            AppServices.Log?.Warn("单实例", $"唤起已有实例失败：{ex.Message}");
        }
    }

    /// <summary>启动管道服务端监听（后台 Task，不占用 UI 线程）。</summary>
    private void StartListener()
    {
        var source = new CancellationTokenSource();
        _listenerSource = source;

        var token = source.Token;
        _listenerTask = Task.Run(() => ListenLoopAsync(token), token);
    }

    /// <summary>管道监听循环：接受连接 → 读一行 → 命中 ACTIVATE 就触发事件。</summary>
    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server, new UTF8Encoding(false));
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(line)
                    && line.Trim().Equals(ActivateCommand, StringComparison.OrdinalIgnoreCase))
                {
                    RaiseActivation();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关机。
                break;
            }
            catch (Exception ex)
            {
                // 管道断开、对方提前关闭、句柄异常——静默重试，绝不让监听线程崩溃。
                AppServices.Log?.Warn("单实例", $"管道监听异常（将重建）：{ex.Message}");
            }
            finally
            {
                if (server is not null)
                {
                    try
                    {
                        server.Dispose();
                    }
                    catch (Exception)
                    {
                        // 释放失败无补救手段。
                    }
                }
            }
        }
    }

    /// <summary>触发唤起事件；订阅方异常不得让监听线程退出。</summary>
    private void RaiseActivation()
    {
        try
        {
            ActivationRequested?.Invoke();
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("单实例", $"唤起回调异常：{ex.Message}");
        }
    }
}
