namespace NoiseCut.Simulator;

public sealed class SimulatorOptions
{
    public const string Section = "Simulator";

    /// <summary>Sim seconds per wall second (SPEC §9). At 10×, an hour-long window closes in 6 real minutes.</summary>
    public double TimeScale { get; set; } = 10;

    /// <summary>Peak order rate. The daily demand curve scales it down off-peak.</summary>
    public double OrdersPerSimMinute { get; set; } = 300;

    public int Seed { get; set; } = 42;

    /// <summary>Fixed sim start, for reproducible experiments. When empty, the simulator resumes after the newest event in the log, or at wall-clock now.</summary>
    public DateTimeOffset? SimStart { get; set; }

    /// <summary>How often a clock.tick goes to every partition (wall time).</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the simulation is advanced and its events produced (wall time).</summary>
    public TimeSpan StepInterval { get; set; } = TimeSpan.FromMilliseconds(100);
}
