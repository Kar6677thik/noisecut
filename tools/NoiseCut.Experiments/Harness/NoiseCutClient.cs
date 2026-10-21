using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoiseCut.Experiments.Harness;

/// <summary>Talks to the stack exactly like an operator would: the Api's admin endpoints, and Prometheus' query API.</summary>
public sealed class NoiseCutClient : IDisposable
{
    private readonly HttpClient _api;
    private readonly HttpClient _prometheus;

    public NoiseCutClient(string apiUrl, string prometheusUrl, string adminKey)
    {
        _api = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(30) };
        _api.DefaultRequestHeaders.Add("X-Admin-Key", adminKey);
        _prometheus = new HttpClient { BaseAddress = new Uri(prometheusUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                var status = await GetJsonAsync("/api/simulator/status");
                if (status["running"]?.GetValue<bool>() == true && (await _api.GetAsync("/readyz")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // not up yet
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("the stack did not become ready");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    public async Task<JsonNode> StartScenarioAsync(string name, string target, double? minutes = null, double? intensity = null)
    {
        var body = new JsonObject { ["target"] = target };
        if (minutes is { } m)
        {
            body["durationMinutes"] = m;
        }

        if (intensity is { } i)
        {
            body["intensity"] = i;
        }

        return await PostAsync($"/api/scenarios/{name}", body);
    }

    public Task<JsonNode> SetRateAsync(double ordersPerSimMinute) =>
        PostAsync("/api/simulator/config", new JsonObject { ["ordersPerSimMinute"] = ordersPerSimMinute });

    public Task<JsonNode> SetChaosAsync(double outOfOrder, double maxHoldBackSeconds, double duplicate) =>
        PostAsync("/api/simulator/chaos", new JsonObject
        {
            ["outOfOrderProbability"] = outOfOrder,
            ["maxHoldBackSeconds"] = maxHoldBackSeconds,
            ["duplicateProbability"] = duplicate,
        });

    public Task<JsonNode> SimulatorStatusAsync() => GetJsonAsync("/api/simulator/status");

    public Task<JsonNode> StatsAsync() => GetJsonAsync("/api/stats");

    public Task<JsonNode> TruthAsync() => GetJsonAsync("/api/truth");

    public Task<JsonNode> AlertsAsync(string ruleId) => GetJsonAsync($"/api/alerts?rule={Uri.EscapeDataString(ruleId)}&limit=500");

    /// <summary>Instant PromQL query that returns one number (or null if the result is empty).</summary>
    public async Task<double?> QueryScalarAsync(string promql)
    {
        var json = await _prometheus.GetFromJsonAsync<JsonNode>($"/api/v1/query?query={Uri.EscapeDataString(promql)}");
        var result = json?["data"]?["result"]?.AsArray();
        if (result is null || result.Count == 0)
        {
            return null;
        }

        var text = result[0]?["value"]?[1]?.GetValue<string>();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    /// <summary>Instant query returning one value per series, keyed by the given label.</summary>
    public async Task<Dictionary<string, double>> QueryByLabelAsync(string promql, string label)
    {
        var json = await _prometheus.GetFromJsonAsync<JsonNode>($"/api/v1/query?query={Uri.EscapeDataString(promql)}");
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var series in json?["data"]?["result"]?.AsArray() ?? [])
        {
            var key = series?["metric"]?[label]?.GetValue<string>() ?? "";
            var text = series?["value"]?[1]?.GetValue<string>();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                values[key] = v;
            }
        }

        return values;
    }

    public void Dispose()
    {
        _api.Dispose();
        _prometheus.Dispose();
    }

    private async Task<JsonNode> GetJsonAsync(string path)
    {
        using var response = await _api.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    private async Task<JsonNode> PostAsync(string path, JsonObject body)
    {
        using var response = await _api.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"POST {path} → {(int)response.StatusCode}: {text}");
        }

        return JsonNode.Parse(text)!;
    }
}
