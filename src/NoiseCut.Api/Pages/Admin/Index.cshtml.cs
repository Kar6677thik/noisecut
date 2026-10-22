using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace NoiseCut.Api.Pages.Admin;

public sealed class IndexModel(AdminGuard guard, SimulatorClient simulator) : PageModel
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [BindProperty]
    public string Scenario { get; set; } = "";

    [BindProperty]
    public string? Target { get; set; }

    [BindProperty]
    public double? DurationMinutes { get; set; }

    public string? Message { get; private set; }

    public bool Ok { get; private set; }

    public string SimulatorStatus { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!guard.IsAdmin(HttpContext))
        {
            return Redirect($"/admin/login?ReturnUrl={Uri.EscapeDataString(Request.Path)}");
        }

        await LoadStatusAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!guard.IsAdmin(HttpContext))
        {
            return Redirect($"/admin/login?ReturnUrl={Uri.EscapeDataString(Request.Path)}");
        }

        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(Target))
        {
            body["target"] = Target.Trim();
        }

        if (DurationMinutes is > 0)
        {
            body["durationMinutes"] = DurationMinutes;
        }

        var (ok, response) = await simulator.PostForPageAsync($"/scenarios/{Uri.EscapeDataString(Scenario)}", body.ToJsonString(), ct);
        Ok = ok;
        Message = ok ? $"Started {Scenario}: {Summarize(response)}" : $"Failed: {response}";
        await LoadStatusAsync(ct);
        return Page();
    }

    private async Task LoadStatusAsync(CancellationToken ct)
    {
        var (ok, body) = await simulator.GetForPageAsync("/status", ct);
        if (!ok)
        {
            SimulatorStatus = $"simulator unreachable: {body}";
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            SimulatorStatus = JsonSerializer.Serialize(doc.RootElement, Pretty);
        }
        catch (JsonException)
        {
            SimulatorStatus = body;
        }
    }

    private static string Summarize(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return $"target {node?["target"]}, until {node?["endsAt"]}";
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
