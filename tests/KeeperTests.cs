using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FudanNetLite;

internal static class KeeperTests
{
    private static readonly Credentials TestCredentials = new Credentials { UserName = "offline-test", Password = "not-a-real-password" };

    public static int Main()
    {
        try
        {
            Run().GetAwaiter().GetResult();
            Console.WriteLine("Keeper: 8 offline tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
    }

    private static async Task Run()
    {
        await OnlineDoesNotLogin();
        await UnknownDoesNotLogin();
        await RetryUsesBoundedBackoff();
        await PermanentFailureStaysBlocked();
        await RestrictedStateEnforcesCooldown();
        await ConcurrentWakeupsAreCoalesced();
        await CancellationStopsWaiting();
        await CancellationStopsInFlightRequest();
    }

    private static async Task OnlineDoesNotLogin()
    {
        var clock = new ManualClock();
        var portal = new FakePortal { State = PortalState.Online };
        using (var keeper = Create(portal, clock))
        {
            Task run = keeper.RunAsync(CancellationToken.None);
            await Until(() => portal.Checks == 1 && clock.Pending == 1);
            clock.Advance(50);
            await Until(() => portal.Checks == 2 && clock.Pending == 1);
            Assert(portal.Logins == 0, "Online state must never submit credentials.");
            keeper.Stop();
            await run;
        }
    }

    private static async Task UnknownDoesNotLogin()
    {
        var clock = new ManualClock();
        var portal = new FakePortal { State = PortalState.Unknown };
        using (var keeper = Create(portal, clock))
        {
            Task run = keeper.RunAsync(CancellationToken.None);
            for (int index = 1; index <= 4; index++)
            {
                int expected = index;
                await Until(() => portal.Checks == expected && clock.Pending == 1);
                clock.Advance(300);
            }
            await Until(() => portal.Checks == 5 && clock.Pending == 1);
            Assert(portal.Logins == 0, "Timeout or unknown status must never trigger a login.");
            keeper.Stop();
            await run;
        }
    }

    private static async Task RetryUsesBoundedBackoff()
    {
        var clock = new ManualClock();
        var portal = new FakePortal();
        using (var keeper = Create(portal, clock))
        {
            Task run = keeper.RunAsync(CancellationToken.None);
            int[] expectedDelays = { 10, 20, 40, 80, 160, 300, 300 };
            for (int index = 0; index < expectedDelays.Length; index++)
            {
                int expectedLogins = index + 1;
                await Until(() => portal.Logins == expectedLogins && clock.Pending == 1);
                Assert(clock.NextDelaySeconds == expectedDelays[index], "Retry backoff must double and cap at five minutes.");
                clock.Advance(expectedDelays[index] - 1);
                Assert(portal.Logins == expectedLogins, "Login occurred before its cooldown expired.");
                clock.Advance(1);
            }
            await Until(() => portal.Logins == 8 && clock.Pending == 1);
            keeper.Stop();
            await run;
        }
    }

    private static async Task PermanentFailureStaysBlocked()
    {
        var clock = new ManualClock();
        var portal = new FakePortal { Result = new LoginResult { PermanentFailure = true, Message = "credential material must not be logged" } };
        var logs = new List<string>();
        using (var keeper = new Keeper(portal, TestCredentials, logs.Add, clock, null, () => 0))
        {
            Task run = keeper.RunAsync(CancellationToken.None);
            await Until(() => portal.Logins == 1 && clock.Pending == 1);
            for (int index = 0; index < 5; index++)
            {
                int expectedChecks = index + 2;
                keeper.ForceCheck();
                await Until(() => clock.NextDelaySeconds == 2);
                clock.Advance(2);
                await Until(() => portal.Checks == expectedChecks && clock.NextDelaySeconds == 50);
            }
            Assert(portal.Logins == 1, "Permanent credential failures must stay blocked despite wakeups.");
            Assert(logs.All(entry => entry.StartsWith("state.") || entry == "login.blocked"), "Logs must contain categories only.");
            keeper.Stop();
            await run;
        }
    }

    private static async Task RestrictedStateEnforcesCooldown()
    {
        var clock = new ManualClock();
        var portal = new FakePortal { State = PortalState.Restricted };
        using (var keeper = Create(portal, clock))
        {
            Task run = keeper.RunAsync(CancellationToken.None);
            await Until(() => portal.Checks == 1 && clock.Pending == 1);
            portal.State = PortalState.Offline;
            keeper.ForceCheck();
            await Until(() => clock.NextDelaySeconds == 2);
            clock.Advance(2);
            await Until(() => portal.Checks == 2 && clock.Pending == 1);
            Assert(portal.Logins == 0, "A wakeup must not bypass a server restriction cooldown.");
            clock.Advance(298);
            await Until(() => portal.Logins == 1 && clock.Pending == 1);
            keeper.Stop();
            await run;
        }
    }

    private static async Task ConcurrentWakeupsAreCoalesced()
    {
        var clock = new ManualClock();
        var gate = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var portal = new FakePortal { LoginHandler = token => gate.Task };
        using (var keeper = Create(portal, clock))
        {
            Task run = keeper.RunAsync(CancellationToken.None);
            await Until(() => portal.Logins == 1);
            Task[] triggers = Enumerable.Range(0, 100).Select(index => Task.Run(() => keeper.ForceCheck())).ToArray();
            await Task.WhenAll(triggers);
            Assert(portal.Logins == 1 && portal.Checks == 1, "Wakeups must not run work concurrently with authentication.");
            portal.State = PortalState.Online;
            gate.SetResult(new LoginResult { Success = true });
            await Until(() => clock.NextDelaySeconds == 2);
            clock.Advance(2);
            await Until(() => portal.Checks == 2 && clock.NextDelaySeconds == 50);
            Assert(portal.Logins == 1, "A burst of wakeups must coalesce into one check.");
            Task duplicate = keeper.RunAsync(CancellationToken.None);
            bool rejected = false;
            try { await duplicate; } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "A second loop must be rejected.");
            keeper.Stop();
            await run;
        }
    }

    private static async Task CancellationStopsWaiting()
    {
        var clock = new ManualClock();
        var portal = new FakePortal { State = PortalState.Online };
        using (var keeper = Create(portal, clock))
        using (var cancelled = new CancellationTokenSource())
        {
            Task run = keeper.RunAsync(cancelled.Token);
            await Until(() => clock.Pending == 1);
            cancelled.Cancel();
            await run;
            Assert(clock.Pending == 0, "Cancellation must remove the waiting timer.");
            clock.Advance(300);
            Assert(portal.Checks == 1, "Cancelled keeper must not keep checking.");
        }
    }

    private static async Task CancellationStopsInFlightRequest()
    {
        var clock = new ManualClock();
        var portal = new FakePortal { CheckHandler = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new PortalStatus { State = PortalState.Online };
        }};
        using (var keeper = Create(portal, clock))
        {
            Task run = keeper.RunAsync(CancellationToken.None);
            await Until(() => portal.Checks == 1);
            keeper.Stop();
            await run;
            Assert(portal.Logins == 0 && clock.Pending == 0, "Stop must cancel in-flight I/O without a login.");
        }
    }

    private static Keeper Create(IPortal portal, IClock clock)
    {
        return new Keeper(portal, TestCredentials, null, clock, null, () => 0);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task Until(Func<bool> condition)
    {
        // Only the scheduler uses real time; every state-machine timer is virtual.
        for (int index = 0; index < 2000; index++)
        {
            if (condition()) return;
            await Task.Delay(1);
        }
        throw new Exception("Timed out awaiting a state-machine transition.");
    }

    private sealed class FakePortal : IPortal
    {
        private int checks, logins;
        public PortalState State = PortalState.Offline;
        public LoginResult Result = new LoginResult { Success = false };
        public Func<CancellationToken, Task<PortalStatus>> CheckHandler;
        public Func<CancellationToken, Task<LoginResult>> LoginHandler;
        public int Checks { get { return Volatile.Read(ref checks); } }
        public int Logins { get { return Volatile.Read(ref logins); } }
        public Task<PortalStatus> CheckAsync(CancellationToken token)
        {
            Interlocked.Increment(ref checks);
            return CheckHandler != null ? CheckHandler(token) : Task.FromResult(new PortalStatus { State = State });
        }
        public Task<LoginResult> LoginAsync(Credentials credentials, CancellationToken token)
        {
            Interlocked.Increment(ref logins);
            return LoginHandler != null ? LoginHandler(token) : Task.FromResult(Result);
        }
    }

    private sealed class ManualClock : IClock
    {
        private readonly object sync = new object();
        private readonly List<Delay> waits = new List<Delay>();
        private DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow { get { lock (sync) return now; } }
        public int Pending { get { lock (sync) return waits.Count(delay => !delay.Completion.Task.IsCompleted); } }
        public double NextDelaySeconds
        {
            get
            {
                lock (sync)
                {
                    Delay next = waits.FirstOrDefault(delay => !delay.Completion.Task.IsCompleted);
                    return next == null ? -1 : (next.Due - now).TotalSeconds;
                }
            }
        }
        public Task DelayAsync(TimeSpan span, CancellationToken token)
        {
            var wait = new Delay();
            lock (sync)
            {
                wait.Due = now.Add(span);
                waits.RemoveAll(delay => delay.Completion.Task.IsCompleted);
                waits.Add(wait);
            }
            wait.Registration = token.Register(() => wait.Completion.TrySetCanceled());
            return DisposeRegistrationAfterCompletion(wait);
        }
        private static async Task DisposeRegistrationAfterCompletion(Delay wait)
        {
            try { await wait.Completion.Task; }
            finally { wait.Registration.Dispose(); }
        }
        public void Advance(double seconds)
        {
            List<Delay> ready;
            lock (sync)
            {
                now = now.AddSeconds(seconds);
                ready = waits.Where(delay => delay.Due <= now).ToList();
            }
            foreach (Delay delay in ready) delay.Completion.TrySetResult(true);
        }
        private sealed class Delay
        {
            public DateTime Due;
            public CancellationTokenRegistration Registration;
            public TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
