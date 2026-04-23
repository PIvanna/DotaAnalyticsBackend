namespace CsgoPredictionSystem.Models;
using Newtonsoft.Json;


public class ExplorerResponse<T>
{
    [JsonProperty("rows")]
    public List<T> Rows { get; set; }
}