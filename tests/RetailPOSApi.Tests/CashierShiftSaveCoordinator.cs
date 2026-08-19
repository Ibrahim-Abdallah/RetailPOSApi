using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RetailPOSApi.Domain;

namespace RetailPOSApi.Tests;

public sealed class CashierShiftSaveCoordinator : SaveChangesInterceptor
{
    readonly object gate = new();
    TaskCompletionSource firstMaySave = NewSignal();
    TaskCompletionSource secondMaySave = NewSignal();
    int arrivals;
    bool enabled;

    public int Arrivals { get; private set; }
    public int DatabaseFailures { get; private set; }

    public void Enable()
    {
        lock (gate)
        {
            firstMaySave = NewSignal();
            secondMaySave = NewSignal();
            arrivals = 0;
            Arrivals = 0;
            DatabaseFailures = 0;
            enabled = true;
        }
    }

    public void Disable()
    {
        lock (gate)
        {
            enabled = false;
            firstMaySave.TrySetResult();
            secondMaySave.TrySetResult();
        }
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Task? wait = null;
        lock (gate)
        {
            if (enabled && eventData.Context!.ChangeTracker.Entries<CashierShift>().Any(x => x.State == EntityState.Added))
            {
                arrivals++;
                Arrivals = arrivals;
                wait = arrivals switch
                {
                    1 => firstMaySave.Task,
                    2 => secondMaySave.Task,
                    _ => throw new InvalidOperationException("Only two coordinated shift inserts are supported.")
                };
                if (arrivals == 2) firstMaySave.TrySetResult();
            }
        }
        if (wait is not null) await wait.WaitAsync(cancellationToken);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (enabled && arrivals == 2 && !secondMaySave.Task.IsCompleted)
                secondMaySave.TrySetResult();
        }
        return ValueTask.FromResult(result);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (enabled && eventData.Context!.ChangeTracker.Entries<CashierShift>().Any()) DatabaseFailures++;
            secondMaySave.TrySetResult();
        }
        return Task.CompletedTask;
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
