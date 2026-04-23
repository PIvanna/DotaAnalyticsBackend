public class SystemStateService
{
    private CancellationTokenSource _cts = new();

    public CancellationToken GlobalCancellationToken => _cts.Token;

    public bool IsMaintenanceMode { get; private set; }

    public void StartMaintenance()
    {
        IsMaintenanceMode = true;
        _cts.Cancel();
        _cts = new CancellationTokenSource(); 
    }

    public void StopMaintenance() => IsMaintenanceMode = false;
}