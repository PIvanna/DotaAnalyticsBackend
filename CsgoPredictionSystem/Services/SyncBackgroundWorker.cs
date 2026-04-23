using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Hubs;
using CsgoPredictionSystem.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CsgoPredictionSystem.Services;

public class SyncBackgroundWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SyncBackgroundWorker> _logger;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30); 

    public SyncBackgroundWorker(IServiceProvider serviceProvider, IHubContext<SyncHub> hubContext, ILogger<SyncBackgroundWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _logger.LogInformation("Sync Background Worker started.");

    while (!stoppingToken.IsCancellationRequested)
    {
        var systemState = _serviceProvider.GetRequiredService<SystemStateService>();

        if (systemState.IsMaintenanceMode)
        {
            _logger.LogWarning("Worker is WAITING: System is in Maintenance Mode (database cleaning).");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            continue;
        }

        try 
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<DotaDbContext>();
                
                var settings = await context.Set<ApiSyncStatus>().ToListAsync(stoppingToken);

                if (!settings.Any())
                {
                    _logger.LogInformation("No sync settings found in DB. Waiting for seed...");
                    await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                    continue;
                }

                foreach (var setting in settings.Where(s => s.IsAutoSyncEnabled))
                {
                    if (systemState.IsMaintenanceMode || stoppingToken.IsCancellationRequested) 
                        break;

                    if (ShouldRun(setting))
                    {
                        var ms = scope.ServiceProvider.GetRequiredService<MatchService>();
                        var ts = scope.ServiceProvider.GetRequiredService<TeamService>();
                        var hs = scope.ServiceProvider.GetRequiredService<HeroService>();
                        var ps = scope.ServiceProvider.GetRequiredService<PlayerService>();
                        var trs = scope.ServiceProvider.GetRequiredService<TournamentService>();

                        await RunSyncTask(setting, ms, ts, hs, ps, trs, context);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in background worker loop");
        }

        await Task.Delay(_checkInterval, stoppingToken);
    }
}

    private bool ShouldRun(ApiSyncStatus setting)
    {
        if (setting.Status == "Pending") return true;

        var timeSinceLastRun = DateTime.UtcNow - setting.LastRunAt.ToUniversalTime();
        
        return setting.SyncType switch
        {
            "Matches" => timeSinceLastRun >= TimeSpan.FromMinutes(30),  
            "Teams" => timeSinceLastRun >= TimeSpan.FromHours(6),      
            "Players" => timeSinceLastRun >= TimeSpan.FromHours(12),   
            "Tournaments" => timeSinceLastRun >= TimeSpan.FromHours(24),
            "Heroes" => timeSinceLastRun >= TimeSpan.FromDays(7),      
            _ => timeSinceLastRun >= TimeSpan.FromHours(1)
        };
    }

    private async Task RunSyncTask(ApiSyncStatus setting, MatchService ms, TeamService ts, HeroService hs, PlayerService ps, TournamentService trs, DotaDbContext context)
    {
        _logger.LogInformation("🔄 Auto-sync: {Type}", setting.SyncType);
        
        setting.Status = "Running";
        await context.SaveChangesAsync();

        try
        {
            string result = setting.SyncType switch
            {
                "Tournaments" => await trs.SyncTournamentsFromApi(50, new[] { "all" }),
                "Heroes" => await hs.SyncHeroesFromApi(),
                "Teams" => await ts.SyncTeamsFromApi(50, 1000, 180),
                "Players" => await ps.SyncPlayersFromApi(100, 180, true),
                "Matches" => await ms.SyncMatchesTask(10, 1, 20),
                _ => "Unknown task"
            };

            setting.LastRunAt = DateTime.UtcNow;
            setting.Status = "Success";
            
            _logger.LogInformation("✅ Auto-synchronization {Type} completed: {Result}", setting.SyncType, result);
        }
        catch (Exception ex)
        {
            setting.Status = "Error";
            _logger.LogError(ex, "❌ Auto-synchronization error {Type}", setting.SyncType);
        }

        await context.SaveChangesAsync();
        
        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
            syncType = setting.SyncType,
            lastRunAt = setting.LastRunAt,
            status = setting.Status,
            isAutoSyncEnabled = setting.IsAutoSyncEnabled
        });
    }
}