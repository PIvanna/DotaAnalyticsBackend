using System.Diagnostics;
using CsgoPredictionSystem.Data;
using Microsoft.EntityFrameworkCore;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using JsonException = System.Text.Json.JsonException;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace CsgoPredictionSystem.Services;

public class PythonMetrics
{
    public double acc  { get; set; }
    public double prec { get; set; }
    public double f1   { get; set; }
}

public class PythonResult
{
    public bool          success     { get; set; }
    public PythonMetrics metrics     { get; set; }
    public int           datasetSize { get; set; }
    public string        error       { get; set; }
}

public class MlPredictionService
{
    private readonly string _pythonPath = "python";
    private readonly string _scriptPath;
    private readonly ILogger<MlPredictionService> _logger;
    private readonly DotaDbContext _context;
    private readonly IHubContext<SyncHub> _hubContext;

    public MlPredictionService(
        DotaDbContext context,
        IHubContext<SyncHub> hubContext,
        ILogger<MlPredictionService> logger)
    {
        _context  = context;
        _hubContext = hubContext;
        _logger   = logger;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var devPath  = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "MLModels", "dota_engine.py"));
        var prodPath = Path.Combine(baseDir, "MLModels", "dota_engine.py");
        _scriptPath  = File.Exists(devPath) ? devPath : prodPath;
    }
    
    private async Task Notify(string message, int progress,
        PythonMetrics metrics = null, int? datasetSize = null)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new
        {
            syncType    = "MLTrain",
            status      = $"{progress}% - {message}",
            progress,
            metrics,
            datasetSize,
            lastRunAt   = DateTime.UtcNow
        });
    }

    private async Task NotifyPredict(string status, int progress, object result = null)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new
        {
            syncType = "MLPredict",
            status,
            progress,
            result,
            lastRunAt = DateTime.UtcNow
        });
    }
    

    public async Task TrainModelInBackground(TrainRequest request, CancellationToken ct = default)
    {
        try
        {
            await Notify("Data preparation and Python launch...", 10);
            ct.ThrowIfCancellationRequested();

            string jsonPayload = JsonSerializer.Serialize(new
            {
                epochs   = request.Epochs,
                features = request.Features,
                userId   = request.UserId
            });

            string rawOutput = await RunPythonEngine("train", jsonPayload, ct);
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = JsonSerializer.Deserialize<PythonResult>(rawOutput);
                if (result != null && result.success)
                    await Notify("Training completed successfully!", 100, result.metrics, result.datasetSize);
                else
                    await Notify($"❌ Script error: {result?.error}", 0);
            }
            catch (JsonException)
            {
                await Notify("❌ Error: Python returned incorrect data.", 0);
            }
        }
        catch (OperationCanceledException)
        {
            await Notify("⚠️ Training canceled.", 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical learning error in the background");
            await Notify($"❌ Critical error: {ex.Message}", 0);
        }
    }
    
    public async Task PredictWinnerInBackground(
        long t1ExtId, long t2ExtId,
        string team1Name, string team2Name,
        string? team1Logo, string? team2Logo,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("=== PREDICT START: {T1} vs {T2} ===", team1Name, team2Name);

            await NotifyPredict("ML handles the request...", 20);
            ct.ThrowIfCancellationRequested();

            string jsonPayload = JsonSerializer.Serialize(new
            {
                team1_id = t1ExtId,
                team2_id = t2ExtId
            });

            string rawOutput = await RunPythonEngine("predict", jsonPayload, ct);
            _logger.LogInformation("PYTHON OUTPUT: {Output}", rawOutput);

            var predResult = JsonConvert.DeserializeObject<PredictionResultDto>(rawOutput);

            if (predResult == null)
            {
                await NotifyPredict("❌ Python returned an empty result.", 0);
                return;
            }

            if (!predResult.success)
            {
                string errMsg = predResult.error ?? "Unknown Python Error";
                _logger.LogError("Python prediction error: {Err}", errMsg);
                await NotifyPredict($"❌ {errMsg}", 0);
                return;
            }

            _logger.LogInformation("Prediction OK: {T1} {C1}% vs {T2} {C2}%",
                team1Name, predResult.team1_win_chance,
                team2Name, predResult.team2_win_chance);

            await NotifyPredict("Prediction Ready", 100, new
            {
                team1 = new
                {
                    name      = team1Name,
                    logo      = team1Logo,
                    externalId = t1ExtId,
                    winChance  = predResult.team1_win_chance
                },
                team2 = new
                {
                    name      = team2Name,
                    logo      = team2Logo,
                    externalId = t2ExtId,
                    winChance  = predResult.team2_win_chance
                },
                h2hHistory  = predResult.h2h_history,
                modelUsed   = predResult.model_used,
                predictedAt = DateTime.UtcNow
            });
        }
        catch (OperationCanceledException)
        {
            await NotifyPredict("⚠️ Forecast cancelled.", 0);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "C# Crash in PredictWinnerInBackground");
            await NotifyPredict($"❌ Forecast error: {ex.Message}", 0);
        }
    }



    private async Task<string> RunPythonEngine(
        string mode, string jsonPayload, CancellationToken ct)
    {
        if (!File.Exists(_scriptPath))
        {
            _logger.LogError("Python script not found: {Path}", _scriptPath);
            return JsonSerializer.Serialize(new
            {
                success = false,
                error   = $"Script not found: {_scriptPath}"
            });
        }

        var startInfo = new ProcessStartInfo
        {
            FileName               = _pythonPath,
            Arguments              = $"\"{_scriptPath}\" {mode}",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
            
            await process.StandardInput.WriteAsync(jsonPayload);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();   

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask  = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _logger.LogWarning("Python process killed due to cancellation/timeout");
                }
                throw;
            }

            string output = await outputTask;
            string error  = await errorTask;

            if (!string.IsNullOrWhiteSpace(error))
                _logger.LogWarning("Python stderr: {Err}", error);

            if (process.ExitCode != 0)
            {
                _logger.LogError("Python exit code {Code}. Stderr: {Err}", process.ExitCode, error);
                return JsonSerializer.Serialize(new { success = false, error });
            }

            int jsonStart = output.IndexOf('{');
            if (jsonStart < 0)
            {
                _logger.LogError("No JSON found in Python output: {Out}", output);
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error   = $"No JSON in output: {output.Trim()}"
                });
            }

            return output[jsonStart..].Trim();
        }
        catch (OperationCanceledException)
        {
            throw; 
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Python process");
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }


    public async Task<object> GetTrainingHistoryPagedAsync(int page, int pageSize, int? userId = null)
    {
        var query = _context.MlTrainingHistory.AsQueryable();

        if (userId.HasValue)
            query = query.Where(h => h.UserId == userId.Value);

        query = query.OrderByDescending(h => h.TrainedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(h => new
            {
                h.SessionId,
                h.Accuracy,
                h.PrecisionScore,
                h.F1Score,
                h.TrainedAt,
                h.DatasetSize,
                h.IsActive,
                h.FeaturesList,
                TrainerName = h.User != null ? h.User.Username : "System"
            })
            .ToListAsync();

        return new { total, page, pageSize, items };
    }



    public async Task<bool> SetActiveModel(int sessionId)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            await _context.Database.ExecuteSqlRawAsync(
                "UPDATE ml_training_history SET is_active = false");

            var model = await _context.MlTrainingHistory.FindAsync(sessionId);
            if (model == null) return false;

            model.IsActive = true;
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return true;
        }
        catch
        {
            await transaction.RollbackAsync();
            return false;
        }
    }



    public class PredictionResultDto
    {
        public bool                success          { get; set; }
        public double              team1_win_chance { get; set; }
        public double              team2_win_chance { get; set; }
        public List<H2HMatchDto>   h2h_history      { get; set; }
        public string              model_used       { get; set; }
        public string              error            { get; set; }
    }

    public class H2HMatchDto
    {
        public long match_id        { get; set; }
        public long radiant_team_id { get; set; }
        public long dire_team_id    { get; set; }
        public int  radiant_win     { get; set; }
    }
}