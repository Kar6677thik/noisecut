using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NoiseCut.Api.Data;

namespace NoiseCut.Api.Pages.Alerts;

public sealed class DetailModel(DashboardQueries queries) : PageModel
{
    public AlertRow? Alert { get; private set; }

    public IReadOnlyList<CandidateRow> Candidates { get; private set; } = [];

    public IReadOnlyList<DeliveryRow> Deliveries { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string key)
    {
        Alert = await queries.AlertAsync(key);
        if (Alert is null)
        {
            return NotFound();
        }

        Candidates = await queries.CandidatesAsync(key);
        Deliveries = await queries.DeliveriesAsync(key);
        return Page();
    }
}
