using Microsoft.Extensions.Hosting;

namespace Meeza.MarketingMcp.Dashboard;

/// <summary>Starts the live dashboard alongside the MCP server (disable with MEEZA_DASHBOARD=off).</summary>
public sealed class DashboardHostedService(DashboardServer server) : IHostedService
{
    public Task StartAsync(CancellationToken ct) =>
        DashboardServer.EnabledWithMcp ? server.StartAsync(ct) : Task.CompletedTask;

    public async Task StopAsync(CancellationToken ct) => await server.DisposeAsync();
}
