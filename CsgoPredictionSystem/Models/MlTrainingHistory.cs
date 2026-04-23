using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CsgoPredictionSystem.Models;

[Table("ml_training_history")]
public class MlTrainingHistory
{
    [Key]
    [Column("session_id")]
    public int SessionId { get; set; }
    
    [Column("trained_at")]
    public DateTime TrainedAt { get; set; } = DateTime.UtcNow;

    [Column("accuracy")]
    public decimal Accuracy { get; set; }

    [Column("precision_score")]
    public decimal PrecisionScore { get; set; }

    [Column("f1_score")]
    public decimal F1Score { get; set; }

    [Column("dataset_size")]
    public int DatasetSize { get; set; }

    [Column("features_list")]
    public string? FeaturesList { get; set; }

    [Column("model_path")]
    public string? ModelPath { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("user_id")]
    public int? UserId { get; set; }
    
    [ForeignKey("UserId")]
    public User? User { get; set; }
}