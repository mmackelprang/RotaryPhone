using System.Collections.Concurrent;

namespace RotaryPhoneController.Core.Sip;

public interface IRegistrarBindingStore
{
    void Record(RegistrarBinding binding);
    void Remove(string addressOfRecord);
    RegistrarBinding? Get(string addressOfRecord);

    /// <summary>
    /// The sole binding, when exactly one endpoint is registered. Single-ATA deployments ring an
    /// extension ("1000") that need not match the AOR the device registered under ("rotaryphone"),
    /// so an exact-match-only lookup would never engage. Returns null when ambiguous.
    /// </summary>
    RegistrarBinding? GetSingle();

    IReadOnlyCollection<RegistrarBinding> All();
}

/// <summary>
/// In-memory registrar binding table. Deliberately not persisted: a binding learned before a restart
/// may be stale, the device re-registers within ~50 minutes of any restart, and the configured
/// address covers that window. Persisting would trade a self-correcting cache for a second stale
/// address store — the exact problem this fixes.
/// </summary>
public sealed class RegistrarBindingStore : IRegistrarBindingStore
{
    private readonly ConcurrentDictionary<string, RegistrarBinding> _bindings =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(RegistrarBinding binding) => _bindings[binding.AddressOfRecord] = binding;

    public void Remove(string addressOfRecord) => _bindings.TryRemove(addressOfRecord, out _);

    public RegistrarBinding? Get(string addressOfRecord) =>
        _bindings.TryGetValue(addressOfRecord, out var binding) ? binding : null;

    public RegistrarBinding? GetSingle() =>
        _bindings.Count == 1 ? _bindings.Values.First() : null;

    public IReadOnlyCollection<RegistrarBinding> All() => _bindings.Values.ToList();
}
