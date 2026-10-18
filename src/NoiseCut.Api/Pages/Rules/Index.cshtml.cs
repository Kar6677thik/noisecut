using System.Text.Json;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NoiseCut.Api.Data;

namespace NoiseCut.Api.Pages.Rules;

public sealed class IndexModel(DashboardQueries queries) : PageModel
{
    public sealed record RuleView(RuleRow Row, string Name, string? Description, string Kind, string Severity);

    public IReadOnlyList<RuleView> Rules { get; private set; } = [];

    public async Task OnGetAsync() => Rules = (await queries.RulesAsync()).Select(ToView).ToList();

    internal static RuleView ToView(RuleRow row)
    {
        using var doc = JsonDocument.Parse(row.Definition);
        var root = doc.RootElement;
        string? Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;
        return new RuleView(row, Get("name") ?? row.Id, Get("description"), Get("kind") ?? "?", Get("severity") ?? "medium");
    }
}
