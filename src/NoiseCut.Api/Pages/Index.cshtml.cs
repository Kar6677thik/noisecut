using Microsoft.AspNetCore.Mvc.RazorPages;
using NoiseCut.Api.Data;

namespace NoiseCut.Api.Pages;

public sealed class IndexModel(DashboardQueries queries) : PageModel
{
    public Stats Stats { get; private set; } = default!;

    public IReadOnlyList<AlertRow> Firing { get; private set; } = [];

    public IReadOnlyList<AlertRow> Recent { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Stats = await queries.StatsAsync();
        Firing = await queries.RecentAlertsAsync(20, status: "firing");
        Recent = await queries.RecentAlertsAsync(25);
    }
}
