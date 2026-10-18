using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NoiseCut.Api.Data;

namespace NoiseCut.Api.Pages.Rules;

public sealed class DetailModel(DashboardQueries queries) : PageModel
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public IndexModel.RuleView? Rule { get; private set; }

    public string PrettyDefinition { get; private set; } = "";

    public IReadOnlyList<AlertRow> Alerts { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string id)
    {
        var row = (await queries.RulesAsync()).SingleOrDefault(r => r.Id == id);
        if (row is null)
        {
            return NotFound();
        }

        Rule = IndexModel.ToView(row);
        using var doc = JsonDocument.Parse(row.Definition);
        PrettyDefinition = JsonSerializer.Serialize(doc.RootElement, Pretty);
        Alerts = await queries.RecentAlertsAsync(50, ruleId: id);
        return Page();
    }
}
