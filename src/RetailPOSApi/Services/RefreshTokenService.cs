using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace RetailPOSApi.Services;

public interface IRefreshTokenService
{
    string Generate();
    string Hash(string rawToken);
}

public sealed class RefreshTokenService : IRefreshTokenService
{
    public string Generate() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));

    public string Hash(string rawToken) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}

public interface IRefreshAttemptCoordinator
{
    Task<RefreshAttempt> BeginAsync(string tokenHash, CancellationToken cancellationToken);
}

public sealed class RefreshAttemptCoordinator : IRefreshAttemptCoordinator
{
    private sealed class AttemptState
    {
        public int ActiveCount { get; set; }
        public bool OverlapOccurred { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    public async Task<RefreshAttempt> BeginAsync(string tokenHash, CancellationToken cancellationToken)
    {
        AttemptState state;
        bool isFirst;
        lock (_gate)
        {
            if (!_attempts.TryGetValue(tokenHash, out state!))
                _attempts[tokenHash] = state = new AttemptState();
            state.ActiveCount++;
            isFirst = state.ActiveCount == 1;
            if (state.ActiveCount > 1) state.OverlapOccurred = true;
        }

        try
        {
            if (isFirst)
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            return new RefreshAttempt(
                () => HasOverlap(state),
                () => Release(tokenHash, state));
        }
        catch
        {
            Release(tokenHash, state);
            throw;
        }
    }

    private bool HasOverlap(AttemptState state)
    {
        lock (_gate) return state.OverlapOccurred;
    }

    private void Release(string tokenHash, AttemptState state)
    {
        lock (_gate)
        {
            if (state.ActiveCount == 0) return;
            state.ActiveCount--;
            if (state.ActiveCount == 0 && _attempts.TryGetValue(tokenHash, out var current) && ReferenceEquals(current, state))
                _attempts.Remove(tokenHash);
        }
    }
}

public sealed class RefreshAttempt(Func<bool> hasOverlappedAnotherAttempt, Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;
    public bool OverlappedAnotherAttempt => hasOverlappedAnotherAttempt();
    public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
}
