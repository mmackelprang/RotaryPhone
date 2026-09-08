namespace RotaryPhoneController.Core.HT801;

/// <summary>
/// One HT801 reachability probe result, as an indivisible unit.
/// </summary>
/// <param name="Reachable">
/// Tri-state, and the null is load-bearing: null means NOT YET PROBED or CANNOT DETERMINE, never
/// offline. Same contract as <see cref="SystemStatus.Ht801Reachable"/>.
/// </param>
/// <param name="LastCheckedUtc">When the probe actually ran — not when someone asked for the answer.</param>
/// <param name="ProbedAddress">
/// The address the probe was aimed at: the RESOLVED registrar binding, i.e. where an INVITE would
/// actually go. Never the configured address — that one was green throughout the 2026-07 outage.
/// </param>
public sealed record Ht801ProbeSnapshot(bool? Reachable, DateTime? LastCheckedUtc, string? ProbedAddress)
{
    /// <summary>The cold-start value. All three null, which the wire contract defines as "Unknown".</summary>
    public static readonly Ht801ProbeSnapshot NotYetProbed = new(null, null, null);
}

/// <summary>
/// The single shared home for the background HT801 reachability probe's latest result, written by
/// the hosted service that runs the probe and read by anyone who has to report reachability.
/// </summary>
public interface IHt801ReachabilityCache
{
    /// <summary>The most recent probe result, or <see cref="Ht801ProbeSnapshot.NotYetProbed"/>.</summary>
    Ht801ProbeSnapshot Current { get; }

    /// <summary>
    /// Stores a probe result. Returns true when <c>Reachable</c> or <c>ProbedAddress</c> changed —
    /// i.e. when there is something worth telling clients about. A refresh that only moves the
    /// timestamp returns false, which is what keeps the SignalR broadcast from firing every 30
    /// seconds on a healthy system.
    ///
    /// <para>
    /// <b>Safe for concurrent writers, and the returned flag is trustworthy under them.</b> The
    /// implementation commits with a compare-and-swap and recomputes the comparison on retry, so the
    /// bool always describes the transition THAT CALL actually committed — never a transition
    /// against a snapshot another writer has already replaced. Without that, two writers could each
    /// see "no change" against the same stale snapshot and a real change would go unbroadcast.
    /// </para>
    /// </summary>
    bool Update(bool? reachable, string probedAddress, DateTime probedAtUtc);
}

/// <summary>
/// Holds the probe result in a SINGLE immutable snapshot behind ONE reference field, read and
/// written with <see cref="Volatile"/>. Both halves of that sentence are the point.
///
/// <para>
/// <b>One reference, volatile, is what makes the cache readable from a request thread at all.</b>
/// This replaces three plain instance fields on SignalRNotifierService that were written from a
/// fire-and-forget thread-pool task and read from the same object. That was fine while the only
/// reader was the notifier itself, but there is no happens-before edge between a probe task's write
/// and an unrelated request thread's read: an ASP.NET request handler could have gone on observing
/// an indefinitely stale value with nothing in the runtime obliged to publish the newer one.
/// </para>
///
/// <para>
/// <b>One snapshot is what makes a torn read impossible.</b> Three independent fields can be
/// observed as a MIX of two probes — a freshly written Reachable paired with the previous
/// LastCheckedUtc, a combination that never existed at any instant. That is precisely the "this
/// field says one thing and the field next to it says another" failure the whole bell-health
/// convergence exists to remove; reintroducing it inside the fix would be absurd. One record behind
/// one reference makes every read internally consistent by construction, with no lock on the read
/// path.
/// </para>
/// </summary>
public sealed class Ht801ReachabilityCache : IHt801ReachabilityCache
{
    private Ht801ProbeSnapshot _current = Ht801ProbeSnapshot.NotYetProbed;

    public Ht801ProbeSnapshot Current => Volatile.Read(ref _current);

    public bool Update(bool? reachable, string probedAddress, DateTime probedAtUtc)
    {
        // The timestamp advances regardless of whether anything else did. Broadcast suppression is
        // about what clients are TOLD, not about what we know — a caller reading Current must always
        // see the true probe age, or the staleness affordance this endpoint exists to feed would
        // freeze at the last change.
        var updated = new Ht801ProbeSnapshot(reachable, probedAtUtc, probedAddress);

        // Read, compare and commit as ONE atomic step, retrying if someone committed in between.
        //
        // Today the only writer is the notifier's probe, which claims a slot with Interlocked before
        // it runs — but that guarantee lives in a different type, in a different assembly, and is
        // invisible from here. This type is public, DI-registered behind a public interface, and the
        // ADR contemplates a POST /api/phone/bell/probe that would be the obvious second writer. A
        // plain read-then-write would then let two probes compute `changed` against the same stale
        // snapshot, and a genuine reachability transition would return false and never be broadcast
        // — a silent stuck-green UI, which is the whole class of bug this work exists to remove.
        //
        // On a 30-second path the loop costs nothing and it never spins in practice: it iterates
        // only when a write genuinely interleaved.
        while (true)
        {
            var previous = Volatile.Read(ref _current);

            // The null -> bool transition counts as a change: it is what turns "Unknown" into a real
            // answer in the UI, and suppressing it would leave the first successful probe invisible.
            // Recomputed on every attempt so it describes the transition we actually commit.
            var changed = reachable != previous.Reachable || probedAddress != previous.ProbedAddress;

            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, updated, previous), previous))
            {
                return changed;
            }
        }
    }
}
