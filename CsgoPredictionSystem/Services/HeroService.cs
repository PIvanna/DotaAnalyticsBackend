using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Models;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Newtonsoft.Json;

namespace CsgoPredictionSystem.Services;

public class HeroService
{
    private readonly DotaDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHubContext<SyncHub> _hubContext;

    public HeroService(DotaDbContext context, IHttpClientFactory httpClientFactory, IHubContext<SyncHub> hubContext)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _hubContext = hubContext;
    }

    private async Task Notify(string message, int progress)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new
        {
            syncType = "Heroes",
            status = $"{progress}% - {message}",
            lastRunAt = DateTime.UtcNow
        });
    }

    public async Task<string> SyncHeroesFromApi(CancellationToken ct = default)
    {
        using var transaction = await _context.Database.BeginTransactionAsync(ct);
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "DotaAnalyticsApp");

            await Notify("Getting data from OpenDota...", 10);

            var response = await client.GetAsync("https://api.opendota.com/api/heroes", ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var apiHeroes = JsonConvert.DeserializeObject<List<HeroApiDto>>(json);

            if (apiHeroes == null || !apiHeroes.Any()) return "Error: No heroes found.";

            ct.ThrowIfCancellationRequested(); 

            await Notify("Updating the list of roles...", 30);
            var allApiRoles = apiHeroes.SelectMany(h => h.Roles).Distinct().ToList();
            
            var existingRoleDefs = await _context.HeroRoleDefinitions.ToDictionaryAsync(r => r.RoleName, ct);

            foreach (var roleName in allApiRoles)
            {
                ct.ThrowIfCancellationRequested(); 
                if (!existingRoleDefs.ContainsKey(roleName))
                {
                    var newRoleDef = new HeroRoleDefinition { RoleName = roleName };
                    _context.HeroRoleDefinitions.Add(newRoleDef);
                    existingRoleDefs[roleName] = newRoleDef;
                }
            }
            await _context.SaveChangesAsync(ct);

            await Notify("Synchronizing heroes...", 60);
            var heroIds = apiHeroes.Select(h => h.Id).ToList();
            var existingHeroes = await _context.Heroes
                .Where(h => heroIds.Contains(h.HeroId))
                .ToDictionaryAsync(h => h.HeroId, ct);

            int added = 0, updated = 0;
            foreach (var h in apiHeroes)
            {
                ct.ThrowIfCancellationRequested();
                if (existingHeroes.TryGetValue(h.Id, out var hero))
                {
                    hero.HeroName = h.Name;
                    hero.LocalizedName = h.LocalizedName;
                    hero.PrimaryAttr = h.PrimaryAttr;
                    updated++;
                }
                else
                {
                    hero = new Hero { HeroId = h.Id, HeroName = h.Name, LocalizedName = h.LocalizedName, PrimaryAttr = h.PrimaryAttr };
                    _context.Heroes.Add(hero);
                    added++;
                }
            }
            await _context.SaveChangesAsync(ct);

            await Notify("Rebuilding connections between heroes and roles...", 80);
            
            await _context.HeroRoles.ExecuteDeleteAsync(ct);

            var dbRoleDefs = await _context.HeroRoleDefinitions.ToDictionaryAsync(r => r.RoleName, r => r.RoleId, ct);
            var newHeroRoles = new List<HeroRole>();

            foreach (var h in apiHeroes)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var roleName in h.Roles)
                {
                    if (dbRoleDefs.TryGetValue(roleName, out int roleId))
                    {
                        newHeroRoles.Add(new HeroRole { HeroId = h.Id, RoleId = roleId });
                    }
                }
            }

            _context.HeroRoles.AddRange(newHeroRoles);
            await _context.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
            await Notify("Synchronization complete!", 100);

            return $"Successful: added {added}, updated {updated}.";
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync();
            throw; 
        }
        catch (Exception ex)
        {
            if (transaction.GetDbTransaction().Connection != null)
                await transaction.RollbackAsync();

            string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            await Notify(friendlyError, 0); 
            throw; 
        }
    }
}