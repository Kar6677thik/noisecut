using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NoiseCut.Api.Data;

namespace NoiseCut.Api.Pages.Alerts;

public sealed class IndexModel(DashboardQueries queries) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Rule { get; set; }

    public IReadOnlyList<AlertRow> Alerts { get; private set; } = [];

    public IReadOnlyList<string> RuleIds { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Alerts = await queries.RecentAlertsAsync(200, NullIfEmpty(Status), NullIfEmpty(Rule));
        RuleIds = (await queries.RulesAsync()).Select(r => r.Id).ToList();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
