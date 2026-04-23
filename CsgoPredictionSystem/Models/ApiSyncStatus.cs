using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CsgoPredictionSystem.Models;

[Table("api_sync_status")]
public class ApiSyncStatus
{
    [Key]
    public string SyncType { get; set; } 
    public DateTime LastRunAt { get; set; }
    public string? Status { get; set; }
    public bool IsAutoSyncEnabled { get; set; } 
}