using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RustServerMetrics.PrometheusMetrics;

internal sealed class DebugEndpointHost : IDisposable
{
    private readonly Func<string> _payloadFactory;
    private HttpListener _listener;
    private CancellationTokenSource _cancellation;
    private Task _serverTask;

    public DebugEndpointHost(Func<string> payloadFactory)
    {
        _payloadFactory = payloadFactory;
    }

    public bool IsRunning { get; private set; }
    public string ListenHost { get; private set; }
    public int ListenPort { get; private set; }
    public string BearerToken { get; private set; }
    public string EndpointPrefix { get; private set; }

    public void Start(string listenHost, int listenPort, string bearerToken)
    {
        Stop();

        ListenHost = listenHost;
        ListenPort = listenPort;
        BearerToken = bearerToken ?? string.Empty;
        EndpointPrefix = BuildPrefix(listenHost, listenPort);

        var listener = new HttpListener();
        listener.Prefixes.Add(EndpointPrefix);

        try
        {
            listener.Start();
        }
        catch
        {
            try
            {
                listener.Close();
            }
            catch
            {
            }

            IsRunning = false;
            throw;
        }

        _listener = listener;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _serverTask = Task.Factory
            .StartNew(() => ServeLoop(cancellation.Token), cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .Unwrap();
        IsRunning = true;
    }

    public void Stop()
    {
        var listener = _listener;
        var cancellation = _cancellation;
        var serverTask = _serverTask;

        _listener = null;
        _cancellation = null;
        _serverTask = null;
        IsRunning = false;

        if (listener == null)
        {
            return;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
        }

        try
        {
            listener.Stop();
        }
        catch
        {
        }

        try
        {
            listener.Close();
        }
        catch
        {
        }

        try
        {
            serverTask?.GetAwaiter().GetResult();
        }
        catch
        {
        }

        try
        {
            cancellation?.Dispose();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task ServeLoop(CancellationToken cancellationToken)
    {
        // TODO: Cover HttpListener start/stop and bearer-auth behavior with a dedicated integration test pass.
        while (!cancellationToken.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                var contextTask = _listener.GetContextAsync();
                context = await contextTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested || _listener == null || !_listener.IsListening)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleRequest(context), cancellationToken);
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 405;
                return;
            }

            if (!IsAuthorized(context.Request))
            {
                context.Response.StatusCode = 401;
                context.Response.AddHeader("WWW-Authenticate", "Bearer");
                return;
            }

            var payload = _payloadFactory.Invoke() ?? "{}";
            var bytes = Encoding.UTF8.GetBytes(payload);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;

            using var output = context.Response.OutputStream;
            output.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrWhiteSpace(BearerToken))
        {
            return true;
        }

        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        return string.Equals(header, "Bearer " + BearerToken, StringComparison.Ordinal);
    }

    private static string NormalizeHost(string host)
    {
        if (string.Equals(host, "0.0.0.0", StringComparison.Ordinal) ||
            string.Equals(host, "*", StringComparison.Ordinal))
        {
            return "+";
        }

        return host;
    }

    private static string BuildPrefix(string listenHost, int listenPort)
    {
        return $"http://{NormalizeHost(listenHost)}:{listenPort}/player-observations/";
    }
}
