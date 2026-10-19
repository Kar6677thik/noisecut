using NoiseCut.Core.Simulation;

namespace NoiseCut.Simulator;

/// <summary>
/// The sim clock: <c>simNow = simStart + (wallNow − wallStart) × TimeScale</c> (SPEC §9).
/// </summary>
public sealed class SimClock(DateTimeOffset simStart, DateTimeOffset wallStart, double timeScale, TimeProvider wall)
{
    public DateTimeOffset SimStart { get; } = simStart;

    public double TimeScale { get; } = timeScale;

    public DateTimeOffset Now => SimStart + ((wall.GetUtcNow() - wallStart) * TimeScale);
}

/// <summary>
/// Owns the one <see cref="OrderSimulation"/> instance. The producer loop and the HTTP control API both touch it,
/// so every access goes through a lock. (The simulation itself is single-threaded and deterministic.)
/// </summary>
public sealed class SimulationHost(TimeProvider wall)
{
    private readonly Lock _gate = new();
    private OrderSimulation? _sim;
    private SimClock? _clock;
    private volatile ChaosSettings _chaos = ChaosSettings.Off;
    private long _burstUntilTicks;

    public long EventsProduced;
    public long TicksProduced;
    public long EventsHeld;
    public long EventsDuplicated;

    public bool IsRunning => _sim is not null;

    public SimClock Clock => _clock ?? throw new InvalidOperationException("simulation not started");

    public ChaosSettings Chaos
    {
        get => _chaos;
        set => _chaos = value;
    }

    /// <summary>Wall time until which every order event is held (a "late burst"); in the past when no burst is active.</summary>
    public DateTimeOffset BurstUntil => new(Interlocked.Read(ref _burstUntilTicks), TimeSpan.Zero);

    public void StartLateBurst(TimeSpan duration) =>
        Interlocked.Exchange(ref _burstUntilTicks, (wall.GetUtcNow() + duration).UtcTicks);

    public void Start(OrderSimulation sim, SimClock clock)
    {
        lock (_gate)
        {
            _sim = sim;
            _clock = clock;
        }
    }

    public void Advance(ICollection<NoiseCut.Core.Events.OrderEvent> output)
    {
        lock (_gate)
        {
            _sim!.AdvanceTo(_clock!.Now, output);
        }
    }

    public ActiveScenario StartScenario(string name, ScenarioRequest request)
    {
        lock (_gate)
        {
            EnsureStarted();
            return _sim!.Scenarios.Start(name, request, _sim.GeneratedUntil);
        }
    }

    public IReadOnlyList<ActiveScenario> Scenarios()
    {
        lock (_gate)
        {
            return _sim is null ? [] : [.. _sim.Scenarios.History];
        }
    }

    public IReadOnlyList<ActiveScenario> ActiveScenarios()
    {
        lock (_gate)
        {
            return _sim is null ? [] : [.. _sim.Scenarios.ActiveAt(_sim.GeneratedUntil)];
        }
    }

    public double OrdersPerSimMinute
    {
        get
        {
            lock (_gate)
            {
                return _sim?.OrdersPerSimMinute ?? 0;
            }
        }
    }

    public void SetRate(double ordersPerSimMinute)
    {
        lock (_gate)
        {
            EnsureStarted();
            _sim!.OrdersPerSimMinute = ordersPerSimMinute;
        }
    }

    public object Status()
    {
        lock (_gate)
        {
            if (_sim is null)
            {
                return new { running = false };
            }

            var now = _sim.GeneratedUntil;
            return new
            {
                running = true,
                simNow = now,
                simStart = _clock!.SimStart,
                timeScale = _clock.TimeScale,
                ordersPerSimMinute = _sim.OrdersPerSimMinute,
                demandFactor = DemandModel.DemandFactor(now),
                ordersCreated = _sim.OrdersCreated,
                pendingSteps = _sim.PendingSteps,
                eventsProduced = Interlocked.Read(ref EventsProduced),
                ticksProduced = Interlocked.Read(ref TicksProduced),
                eventsHeld = Interlocked.Read(ref EventsHeld),
                eventsDuplicated = Interlocked.Read(ref EventsDuplicated),
                chaos = _chaos,
                lateBurstUntil = BurstUntil > wall.GetUtcNow() ? BurstUntil : (DateTimeOffset?)null,
                activeScenarios = _sim.Scenarios.ActiveAt(now).ToList(),
            };
        }
    }

    private void EnsureStarted()
    {
        if (_sim is null)
        {
            throw new InvalidOperationException("the simulation has not started yet (waiting for Kafka)");
        }
    }
}
