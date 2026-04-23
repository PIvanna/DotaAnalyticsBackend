
namespace CsgoPredictionSystem.DTO;

public class TrainRequest
{
    public int Epochs { get; set; }
    public List<string> Features { get; set; } = new();
    public int UserId { get; set; }
}
