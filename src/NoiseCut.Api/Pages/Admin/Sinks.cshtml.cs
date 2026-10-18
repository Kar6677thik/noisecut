using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NoiseCut.Api.Data;

namespace NoiseCut.Api.Pages.Admin;

public sealed class SinksModel(AdminGuard guard, DashboardQueries queries) : PageModel
{
    public Stats Stats { get; private set; } = default!;

    public IReadOnlyList<ReceiptRow> Receipts { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!guard.IsAdmin(HttpContext))
        {
            return NotFound();
        }

        Stats = await queries.StatsAsync();
        Receipts = await queries.ReceiptsAsync(200);
        return Page();
    }
}
