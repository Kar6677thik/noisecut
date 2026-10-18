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
public sealed class SimulationHost
{
    private readonly Lock _gate = new();
    private OrderSimulation? _sim;
    private SimClock? _clock;

    public long EventsProduced;
    public long TicksProduced;

    public bool IsRunning => _sim is not null;

    public SimClock Clock => _clock ?? throw new InvalidOperationException("simulation not started");

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
