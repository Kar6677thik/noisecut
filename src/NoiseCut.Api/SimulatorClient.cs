using System.Text;

namespace NoiseCut.Api;

/// <summary>Forwards admin requests to the simulator's internal control API and relays its answer unchanged.</summary>
public sealed class SimulatorClient(HttpClient http)
{
    public async Task<IResult> ForwardAsync(string path, string jsonBody, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(path, content, ct);
            return await RelayAsync(response, ct);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem($"simulator unreachable: {ex.Message}", statusCode: 502);
        }
    }

    public async Task<IResult> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(path, ct);
            return await RelayAsync(response, ct);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem($"simulator unreachable: {ex.Message}", statusCode: 502);
        }
    }

    public async Task<(bool Ok, string Body)> PostForPageAsync(string path, string jsonBody, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(path, content, ct);
            return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Body)> GetForPageAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(path, ct);
            return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<IResult> RelayAsync(HttpResponseMessage response, CancellationToken ct) =>
        Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json", Encoding.UTF8, (int)response.StatusCode);
}
