using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Tests;

public sealed class SaleMutationSaveCoordinator : SaveChangesInterceptor
{
    volatile bool enabled;
    int arrivals;
    TaskCompletionSource bothArrived = NewSignal();
    TaskCompletionSource winnerSaved = NewSignal();

    public int Arrivals => Volatile.Read(ref arrivals);

    public void Enable()
    {
        arrivals = 0;
        bothArrived = NewSignal();
        winnerSaved = NewSignal();
        enabled = true;
    }

    public void Disable()
    {
        enabled = false;
        bothArrived.TrySetResult();
        winnerSaved.TrySetResult();
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (!enabled || eventData.Context is null || !eventData.Context.ChangeTracker.Entries<Sale>().Any(x => x.State == EntityState.Modified))
            return result;
        var arrival = Interlocked.Increment(ref arrivals);
        if (arrival == 2) bothArrived.TrySetResult();
        await bothArrived.Task.WaitAsync(ct);
        if (arrival == 2)
        {
            await winnerSaved.Task.WaitAsync(ct);
            throw new DbUpdateConcurrencyException("Deterministic test stale Sale rowversion.");
        }
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        if (enabled && Arrivals > 0) winnerSaved.TrySetResult();
        return ValueTask.FromResult(result);
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
