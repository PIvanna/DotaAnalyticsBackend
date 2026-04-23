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
    public double acc { get; set; }
    public double prec { get; set; }
    public double f1 { get; set; }
}

public class PythonResult
{
    public bool success { get; set; }
    public PythonMetrics metrics { get; set; }
    public int datasetSize { get; set; }
    public string error { get; set; }
}

public class MlPredictionService
{
    private readonly string _pythonPath = "python"; 
    private readonly string _scriptPath;
    private readonly ILogger<MlPredictionService> _logger;
    private readonly DotaDbContext _context;
    private readonly IHubContext<SyncHub> _hubContext;

    public MlPredictionService(DotaDbContext context, IHubContext<SyncHub> hubContext, ILogger<MlPredictionService> logger)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var devPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "MLModels", "dota_engine.py"));
        var prodPath = Path.Combine(baseDir, "MLModels", "dota_engine.py");

        _scriptPath = File.Exists(devPath) ? devPath : prodPath;
    }
    
    private async Task Notify(string message, int progress, PythonMetrics metrics = null, int? datasetSize = null)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
            syncType = "MLTrain",
            status = $"{progress}% - {message}",
            progress = progress,
            metrics = metrics, 
            datasetSize = datasetSize,
            lastRunAt = DateTime.UtcNow
        });
    }

    public async Task TrainModelInBackground(TrainRequest request, CancellationToken ct = default)
    {
        try 
        {
            await Notify("Data preparation and Python launch...", 10);
            ct.ThrowIfCancellationRequested();

            
            string jsonPayload = JsonSerializer.Serialize(new {
                epochs = request.Epochs,
                features = request.Features,
                userId = request.UserId
            });

            string rawOutput = await RunPythonEngine("train", jsonPayload, ct);
            
            ct.ThrowIfCancellationRequested();

            
            try 
            {
                var result = JsonSerializer.Deserialize<PythonResult>(rawOutput);
                
                if (result != null && result.success)
                {
                    await Notify("Training completed successfully!", 100, result.metrics, result.datasetSize);
                }
                else
                {
                    await Notify($"❌  Script error: {result?.error}", 0);
                }
            }
            catch (JsonException)
            {
                await Notify("❌ Error: Python returned incorrect data.", 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical background learning error");
            await Notify($"❌ Critical error: {ex.Message}", 0);
        }
    }


   private async Task<string> RunPythonEngine(string mode, string jsonPayload, CancellationToken ct)
{
    if (!File.Exists(_scriptPath))
    {
        return JsonSerializer.Serialize(new { success = false, error = $"Script not found" });
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = _pythonPath,
        Arguments = $"\"{_scriptPath}\" {mode}", 
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true, 
        CreateNoWindow = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8 
    };

    using var process = new Process { StartInfo = startInfo };

    try
    {
        process.Start();

        using (var sw = process.StandardInput)
        {
            if (sw.BaseStream.CanWrite)
            {
                await sw.WriteAsync(jsonPayload);
                await sw.FlushAsync();
            }
        } 

        var readOutputTask = process.StandardOutput.ReadToEndAsync(ct);
        var readErrorTask = process.StandardError.ReadToEndAsync(ct);

        try 
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }

        string output = await readOutputTask;
        string error = await readErrorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("Python Error: {Error}", error);
            return JsonSerializer.Serialize(new { success = false, error = error });
        }

        int jsonStartIndex = output.IndexOf('{');
        return jsonStartIndex >= 0 ? output.Substring(jsonStartIndex).Trim() : output.Trim();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to run Python via stdin");
        return JsonSerializer.Serialize(new { success = false, error = ex.Message });
    }
}

    public async Task<object> GetTrainingHistoryPagedAsync(int page, int pageSize, int? userId = null)
    {
        var query = _context.MlTrainingHistory.AsQueryable();

        if (userId.HasValue)
        {
            query = query.Where(h => h.UserId == userId.Value);
        }

        query = query.OrderByDescending(h => h.TrainedAt);

        var totalCount = await query.CountAsync();
    
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(h => new {
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

        return new { total = totalCount, page, pageSize, items };
    }
    
    public async Task PredictWinnerInBackground(long t1ExtId, long t2ExtId, CancellationToken ct = default)
{
    try 
    {
        _logger.LogInformation("--- START PREDICTION LOG ---");

        ct.ThrowIfCancellationRequested();

        
        string jsonPayload = JsonSerializer.Serialize(new {
            team1_id = t1ExtId,
            team2_id = t2ExtId
        });

        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
            syncType = "MLPredict",
            status = "Consulting the Oracle...",
            progress = 20
        });

        string rawOutput = await RunPythonEngine("predict", jsonPayload, ct);
        _logger.LogInformation("PYTHON RAW OUTPUT: {Output}", rawOutput);

        var predictionResult = JsonConvert.DeserializeObject<PredictionResultDto>(rawOutput);

        if (predictionResult != null && predictionResult.success)
        {
            _logger.LogInformation("Calculation Success: T1: {C1}%, T2: {C2}%", 
                predictionResult.team1_win_chance, predictionResult.team2_win_chance);

            await Task.Delay(1000); 
            await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
                syncType = "MLPredict",
                status = "Prediction Ready",
                progress = 100,
                result = predictionResult 
            });
        }
        else
        {
            string errorText = predictionResult?.error ?? "Unknown Python Error";
            _logger.LogError("Python Logic Error: {Error}", errorText);

            await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
                syncType = "MLPredict",
                status = "❌ " + errorText,
                progress = 0
            });
        }
    }
    catch (Exception ex)
    {
        _logger.LogCritical(ex, "C# Crash in Predict");

        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
            syncType = "MLPredict",
            status = "❌ Prediction Error: " + ex.Message,
            progress = 0
        });
    }
}

    public async Task<bool> SetActiveModel(int sessionId)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            await _context.Database.ExecuteSqlRawAsync("UPDATE ml_training_history SET is_active = false");
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
        public bool success { get; set; }
        public double team1_win_chance { get; set; }
        public double team2_win_chance { get; set; }
        public List<H2HMatchDto> h2h_history { get; set; }
        public string model_used { get; set; }
        public string error { get; set; }
    }

    public class H2HMatchDto
    {
        public long match_id { get; set; }
        public long radiant_team_id { get; set; }
        public long dire_team_id { get; set; }
        public int radiant_win { get; set; }
    }
}