using System;
using System.Net;
using System.Net.Sockets;
using RustServerMetrics.PrometheusMetrics;
using Xunit;

namespace RustServerMetrics.Tests;

public sealed class PrometheusExporterHostTests
{
    [Fact]
    public void StopAfterFailedStartDoesNotThrowAndHostCanRetry()
    {
        var port = GetFreeTcpPort();
        using var owner = new PrometheusExporterHost(new MetricRegistry());
        using var contender = new PrometheusExporterHost(new MetricRegistry());

        try
        {
            owner.Start("127.0.0.1", port, "/metrics");

            var startException = Record.Exception(() => contender.Start("127.0.0.1", port, "/metrics"));
            Assert.NotNull(startException);
            Assert.False(contender.IsRunning);
            Assert.Null(Record.Exception(contender.Stop));

            owner.Stop();

            Assert.Null(Record.Exception(() => contender.Start("127.0.0.1", port, "/metrics")));
            Assert.True(contender.IsRunning);
            Assert.Equal($"http://127.0.0.1:{port}/metrics/", contender.EndpointPrefix);
        }
        finally
        {
            contender.Stop();
            owner.Stop();
        }
    }

    [Fact]
    public void StartReplacesExistingListener()
    {
        var port = GetFreeTcpPort();
        using var host = new PrometheusExporterHost(new MetricRegistry());

        try
        {
            host.Start("127.0.0.1", port, "/metrics");
            host.Start("127.0.0.1", port, "/metrics");

            Assert.True(host.IsRunning);
            Assert.Equal($"http://127.0.0.1:{port}/metrics/", host.EndpointPrefix);
        }
        finally
        {
            host.Stop();
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
