using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core;

public class CallAdapterRegistry : ICallAdapterRegistry
{
    private readonly Dictionary<CallAdapterMode, ICallAdapter> _adapters = new();
    private ICallAdapter? _activeAdapter;
    private readonly ILogger<CallAdapterRegistry>? _logger;

    public CallAdapterMode ActiveMode { get; private set; }

    public ICallAdapter ActiveAdapter =>
        _activeAdapter ?? throw new InvalidOperationException("No adapter is active. Call SwitchModeAsync first.");

    public IReadOnlyList<CallAdapterMode> AvailableModes =>
        _adapters.Keys.ToList().AsReadOnly();

    public event Action<CallAdapterMode>? OnModeChanged;

    public CallAdapterRegistry(ILogger<CallAdapterRegistry>? logger = null)
    {
        _logger = logger;
    }

    public void Register(ICallAdapter adapter)
    {
        _adapters[adapter.Mode] = adapter;
        _logger?.LogInformation("Registered call adapter: {Mode}", adapter.Mode);
    }

    public async Task SwitchModeAsync(CallAdapterMode mode, CancellationToken ct = default)
    {
        if (!_adapters.TryGetValue(mode, out var newAdapter))
            throw new InvalidOperationException($"No adapter registered for mode: {mode}");

        if (_activeAdapter != null && _activeAdapter.Mode != mode)
        {
            _logger?.LogInformation("Deactivating adapter: {Mode}", _activeAdapter.Mode);
            await _activeAdapter.DeactivateAsync(ct);
        }

        _activeAdapter = newAdapter;
        ActiveMode = mode;

        _logger?.LogInformation("Activating adapter: {Mode}", mode);
        await newAdapter.ActivateAsync(ct);

        OnModeChanged?.Invoke(mode);
        _logger?.LogInformation("Call adapter mode switched to: {Mode}", mode);
    }
}
