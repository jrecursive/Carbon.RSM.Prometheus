using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RustServerMetrics.PrometheusMetrics;

internal sealed class PrometheusExporterHost : IDisposable
{
    private readonly MetricRegistry _registry;
    private HttpListener _listener;
    private CancellationTokenSource _cancellation;
    private Task _serverTask;

    public PrometheusExporterHost(MetricRegistry registry)
    {
        _registry = registry;
    }

    public bool IsRunning { get; private set; }
    public string ListenHost { get; private set; }
    public int ListenPort { get; private set; }
    public string MetricsPath { get; private set; }

    public void Start(string listenHost, int listenPort, string metricsPath)
    {
        Stop();

        ListenHost = listenHost;
        ListenPort = listenPort;
        MetricsPath = metricsPath;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{NormalizeMetricServerHost(listenHost)}:{listenPort}/{NormalizeMetricServerPath(metricsPath)}");
        _listener.Start();

        _cancellation = new CancellationTokenSource();
        _serverTask = Task.Factory.StartNew(() => ServeLoop(_cancellation.Token), TaskCreationOptions.LongRunning);
        IsRunning = true;
    }

    public void Stop()
    {
        if (_listener == null)
        {
            IsRunning = false;
            return;
        }

        _cancellation.Cancel();

        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            _listener.Close();
        }
        catch
        {
        }

        try
        {
            _serverTask?.GetAwaiter().GetResult();
        }
        catch
        {
        }

        _cancellation.Dispose();
        _listener = null;
        _cancellation = null;
        _serverTask = null;
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task ServeLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
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

            var payload = _registry.CollectAsText();
            var bytes = Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
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

    private static string NormalizeMetricServerHost(string host)
    {
        if (string.Equals(host, "0.0.0.0", StringComparison.Ordinal) ||
            string.Equals(host, "*", StringComparison.Ordinal))
        {
            return "+";
        }

        return host;
    }

    private static string NormalizeMetricServerPath(string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "metrics" : path.Trim().Trim('/');
        if (normalized.Length == 0)
        {
            normalized = "metrics";
        }

        return normalized + "/";
    }
}
