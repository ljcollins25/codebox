using System.Security.Cryptography;
using System.Text;

namespace AzureDevops.Pipeline.Utilities;

public class IncomingWebhook
{
    public record BinaryContent(byte[] Bytes)
    {
        public static implicit operator BinaryContent(string s) => Encoding.UTF8.GetBytes(s);
        public static implicit operator BinaryContent(byte[] b) => new BinaryContent(b);
    }

    /// <summary>
    /// Sends a payload to Azure DevOps Incoming Webhook.
    /// </summary>
    /// <param name="organization">ADO org (e.g. "contoso")</param>
    /// <param name="webhookName">Webhook name (from ADO Incoming Webhook)</param>
    /// <param name="secret">Shared secret configured on the ADO Incoming Webhook</param>
    /// <param name="data">JSON string to send (will be sent as-is)</param>
    /// <param name="headerName">Header ADO validates (default "X-WH-Checksum")</param>
    /// <param name="apiVersion">API version (default "7.2-preview.2")</param>
    public static async Task SendAsync(
        string organization,
        string webhookName,
        BinaryContent secret,
        BinaryContent data,
        string headerName = "X-WH-Checksum",
        string apiVersion = "7.2-preview.2")
    {
        // Build URL
        var url = $"https://dev.azure.com/{organization}/_apis/public/distributedtask/webhooks/{webhookName}?api-version={Uri.EscapeDataString(apiVersion)}";

        // Raw UTF-8 bytes exactly as sent
        var bodyBytes = data.Bytes;

        // HMAC-SHA1 over the raw body with the ADO Incoming Webhook secret
        using var hmac = new HMACSHA1(secret.Bytes);
        var hash = hmac.ComputeHash(data.Bytes);

        var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); // bare hex, no "sha1="

        using var http = new HttpClient();
        using var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation(headerName, "sha1=" + hex);

        var resp = await http.SendAsync(req);
        var respText = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ADO webhook returned {(int)resp.StatusCode} {resp.StatusCode}:\n{respText}");
        }

        Console.WriteLine($"OK {(int)resp.StatusCode} {resp.StatusCode}");
        if (!string.IsNullOrWhiteSpace(respText)) Console.WriteLine(respText);
    }
}