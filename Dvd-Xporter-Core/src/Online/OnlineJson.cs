using System.Globalization;
using System.Net;
using System.Text.Json;

namespace DvdXporter.Core.Online;

/// <summary>Small helpers for reading loosely-typed JSON API responses.</summary>
internal static class OnlineJson
{
    public static async Task<JsonElement> GetAsync(HttpClient http, HttpRequestMessage request, string serviceName,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OnlineMetadataException($"Couldn't reach {serviceName}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OnlineMetadataException($"{serviceName} took too long to respond.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonElement json;
            try
            {
                json = JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new OnlineMetadataException($"{serviceName} sent an unexpected response ({(int)response.StatusCode}).", ex);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized)
                throw new OnlineMetadataException($"{serviceName} rejected the API key. Check it in Settings.");
            if (!response.IsSuccessStatusCode)
            {
                var message = String(json, "status_message") ?? String(json, "Error") ?? String(json, "message") ?? response.ReasonPhrase;
                throw new OnlineMetadataException($"{serviceName} returned an error: {message}");
            }

            return json;
        }
    }

    public static string? String(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        // OMDb uses "N/A" for missing values.
        return string.IsNullOrWhiteSpace(text) || text == "N/A" ? null : text.Trim();
    }

    public static int? Int(JsonElement element, string property) =>
        int.TryParse(String(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static double Double(JsonElement element, string property) =>
        double.TryParse(String(element, property), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    public static JsonElement Object(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    /// <summary>Parses "2002-09-20" (TMDB, TVDB, OMDb episodes) or "20 Sep 2002" (OMDb titles).</summary>
    public static DateOnly? Date(string? text)
    {
        if (text is null)
            return null;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (DateOnly.TryParseExact(text, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return date;
        return null;
    }

    /// <summary>The first four-digit year in text like "2002", "2002-09-20" or "2002–2003".</summary>
    public static int? Year(string? text) =>
        text is { Length: >= 4 } && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
}
