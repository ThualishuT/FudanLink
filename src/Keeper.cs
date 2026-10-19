using System;
using System.Threading;
using System.Threading.Tasks;

namespace FudanNetLite
{
    public enum PortalState { Online, Offline, Unknown, Restricted }

    public sealed class Credentials
    {
        public string UserName { get; set; }
        public string Password { get; set; }
    }

    public sealed class PortalStatus
    {
        public PortalState State { get; set; }
        public string Message { get; set; }
    }

    public sealed class LoginResult
    {
        public bool Success { get; set; }
        public bool PermanentFailure { get; set; }
        public string Message { get; set; }
    }

    public interface IPortal
    {
        Task<PortalStatus> CheckAsync(CancellationToken cancellationToken);
        Task<LoginResult> LoginAsync(Credentials credentials, CancellationToken cancellationToken);
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow { get { return DateTime.UtcNow; } }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    public sealed class KeeperOptions
    {
        public TimeSpan CheckInterval { get; set; }
        public TimeSpan WakeDebounce { get; set; }
        public TimeSpan InitialBackoff { get; set; }
        public TimeSpan MaximumBackoff { get; set; }

        public KeeperOptions()
        {
            CheckInterval = TimeSpan.FromSeconds(50);
            WakeDebounce = TimeSpan.FromSeconds(2);
            InitialBackoff = TimeSpan.FromSeconds(10);
            MaximumBackoff = TimeSpan.FromMinutes(5);
        }
    }

    // One serial loop owns all authentication state. ForceCheck only wakes that loop;
    // it cannot bypass login cooldowns or the permanent-failure latch.
    public sealed class Keeper : IDisposable
    {
        private readonly IPortal portal;
        private readonly Credentials credentials;
        private readonly IClock clock;
        private readonly Action<string> log;
        private readonly Func<double> random;
        private readonly TimeSpan interval, debounce, initialBackoff, maximumBackoff;
        private readonly CancellationTokenSource stopped = new CancellationTokenSource();
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
        private int running;
        private int disposed;
        private int loginFailures;
        private int unknownFailures;
        private bool loginBlocked;
        private DateTime nextLoginUtc = DateTime.MinValue;
        private PortalState? previousState;

        public Keeper(IPortal portal, Credentials credentials, Action<string> log = null,
            IClock clock = null, KeeperOptions options = null, Func<double> random = null)
        {
            if (portal == null) throw new ArgumentNullException("portal");
            options = options ?? new KeeperOptions();
            if (options.CheckInterval <= TimeSpan.Zero || options.WakeDebounce < TimeSpan.Zero ||
                options.InitialBackoff <= TimeSpan.Zero || options.MaximumBackoff < options.InitialBackoff ||
                options.MaximumBackoff > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException("options");
            this.portal = portal;
            this.credentials = credentials == null ? new Credentials() : new Credentials
                { UserName = credentials.UserName, Password = credentials.Password };
            this.log = log;
            this.clock = clock ?? new SystemClock();
            var generator = new Random();
            this.random = random ?? generator.NextDouble;
            interval = options.CheckInterval;
            debounce = options.WakeDebounce;
            initialBackoff = options.InitialBackoff;
            maximumBackoff = options.MaximumBackoff;
        }

        public void ForceCheck()
        {
            if (Volatile.Read(ref disposed) != 0 || stopped.IsCancellationRequested) return;
            try { wake.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public void Stop()
        {
            if (Volatile.Read(ref disposed) == 0) stopped.Cancel();
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("Keeper");
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                throw new InvalidOperationException("Keeper is already running.");
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopped.Token))
                {
                    try
                    {
                        while (true)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            TimeSpan delay = await CheckOnceAsync(linked.Token).ConfigureAwait(false);
                            await WaitForNextAsync(delay, linked.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (!linked.IsCancellationRequested) throw;
                    }
                }
            }
            finally { Interlocked.Exchange(ref running, 0); }
        }

        private async Task<TimeSpan> CheckOnceAsync(CancellationToken cancellationToken)
        {
            PortalStatus status;
            try { status = await portal.CheckAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                status = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            PortalState state = status == null ? PortalState.Unknown : status.State;
            if (previousState != state)
            {
                WriteLog("state." + state.ToString().ToLowerInvariant());
                previousState = state;
            }

            if (state == PortalState.Online)
            {
                loginFailures = unknownFailures = 0;
                nextLoginUtc = DateTime.MinValue;
                return interval;
            }
            if (state == PortalState.Unknown)
            {
                unknownFailures = Math.Min(unknownFailures + 1, 30);
                return Max(interval, Backoff(unknownFailures));
            }
            unknownFailures = 0;
            if (state == PortalState.Restricted)
            {
                nextLoginUtc = clock.UtcNow.Add(maximumBackoff);
                return maximumBackoff;
            }
            if (state != PortalState.Offline || loginBlocked ||
                String.IsNullOrWhiteSpace(credentials.UserName) || String.IsNullOrEmpty(credentials.Password))
                return interval;
            DateTime now = clock.UtcNow;
            if (now < nextLoginUtc)
                return Min(interval, nextLoginUtc - now);

            LoginResult result;
            try { result = await portal.LoginAsync(credentials, cancellationToken).ConfigureAwait(false); }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (result != null && result.Success)
            {
                WriteLog("login.accepted");
                loginFailures = 0;
                // Wait for the next status check to confirm online; a successful reply
                // alone must not permit repeated immediate login after network events.
                nextLoginUtc = clock.UtcNow.Add(interval);
                return interval;
            }
            if (result != null && result.PermanentFailure)
            {
                loginBlocked = true;
                WriteLog("login.blocked");
                return interval;
            }
            loginFailures = Math.Min(loginFailures + 1, 30);
            TimeSpan retryDelay = Backoff(loginFailures);
            nextLoginUtc = clock.UtcNow.Add(retryDelay);
            WriteLog("login.transient_failure");
            return retryDelay;
        }

        private TimeSpan Backoff(int failures)
        {
            double baseMs = Math.Min(maximumBackoff.TotalMilliseconds,
                initialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 20)));
            // Positive-only jitter preserves the minimum cooldown and the hard cap.
            double sample = Math.Max(0, Math.Min(1, random()));
            return TimeSpan.FromMilliseconds(Math.Min(maximumBackoff.TotalMilliseconds, baseMs * (1 + sample * .2)));
        }

        private async Task WaitForNextAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            using (var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task timer = clock.DelayAsync(delay, race.Token);
                Task signal = wake.WaitAsync(race.Token);
                Task completed = await Task.WhenAny(timer, signal).ConfigureAwait(false);
                bool woke = completed == signal && signal.Status == TaskStatus.RanToCompletion;
                race.Cancel();
                // Observe both tasks and remove the losing waiter before the next loop.
                try { await Task.WhenAll(timer, signal).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
                if (woke)
                {
                    if (debounce > TimeSpan.Zero)
                        await clock.DelayAsync(debounce, cancellationToken).ConfigureAwait(false);
                    while (wake.Wait(0)) { }
                }
            }
        }

        private void WriteLog(string category)
        {
            if (log == null) return;
            // Logging must never interrupt connectivity maintenance. No server messages,
            // URLs, usernames, passwords, or response bodies are passed to this callback.
            try { log(category); } catch (Exception) { }
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) { return a < b ? a : b; }
        private static TimeSpan Max(TimeSpan a, TimeSpan b) { return a > b ? a : b; }

        public void Dispose()
        {
            // Stop is asynchronous: the caller should await RunAsync before Dispose.
            if (Volatile.Read(ref running) != 0)
                throw new InvalidOperationException("Stop and await RunAsync before disposing Keeper.");
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                stopped.Cancel();
                stopped.Dispose();
                wake.Dispose();
            }
        }
    }
}
