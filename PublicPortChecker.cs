using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace Local_Port_Utility;

/// Looks up the public IPv4 address and asks Check-Host to probe a port externally
internal sealed class PublicPortChecker
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<List<PublicPortTestResult>> CheckAsync(int port)
    {
        // ipify only reports the public IPv4 address it does not test whether the port is reachable
        // Routeri obično ne daju javni IP računaru direktno pa se adresa čita preko HTTPS servisa
        // This endpoint forces IPv4 which is the address family sent to the probe service below
        using var ipResponse = await HttpClient.GetAsync("https://api4.ipify.org");
        ipResponse.EnsureSuccessStatusCode();
        var publicAddress = (await ipResponse.Content.ReadAsStringAsync()).Trim();
        if (!IPAddress.TryParse(publicAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("The public IP lookup did not return a valid IPv4 address.");

        // Check-Host pokreće probu sa spoljašnje mreže TCP i UDP traže različite vrste odgovora
        var tcpTask = CheckProtocolAsync("tcp", "TCP", publicAddress, port);
        var udpTask = CheckProtocolAsync("udp", "UDP", publicAddress, port);
        return (await Task.WhenAll(tcpTask, udpTask)).ToList();
    }

    private static async Task<PublicPortTestResult> CheckProtocolAsync(string checkType, string protocol, string publicAddress, int port)
    {
        try
        {
            var checkUrl = $"https://check-host.net/check-{checkType}?host=" + Uri.EscapeDataString($"{publicAddress}:{port}") + "&max_nodes=1";
            using var startRequest = new HttpRequestMessage(HttpMethod.Get, checkUrl);
            startRequest.Headers.Accept.ParseAdd("application/json");
            using var startResponse = await HttpClient.SendAsync(startRequest);
            startResponse.EnsureSuccessStatusCode();
            using var startDocument = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
            if (!startDocument.RootElement.TryGetProperty("request_id", out var requestIdElement))
                throw new InvalidOperationException("Check-Host.net did not return a check request ID.");
            var requestId = requestIdElement.GetString();
            if (string.IsNullOrWhiteSpace(requestId)) throw new InvalidOperationException("Check-Host.net returned an invalid request ID.");

            // The API gives back a request ID first poll briefly until its external probe finishes
            var resultUrl = "https://check-host.net/check-result/" + Uri.EscapeDataString(requestId);
            for (var attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                using var resultRequest = new HttpRequestMessage(HttpMethod.Get, resultUrl);
                resultRequest.Headers.Accept.ParseAdd("application/json");
                using var resultResponse = await HttpClient.SendAsync(resultRequest);
                resultResponse.EnsureSuccessStatusCode();
                using var resultDocument = JsonDocument.Parse(await resultResponse.Content.ReadAsStringAsync());
                foreach (var nodeResult in resultDocument.RootElement.EnumerateObject().SelectMany(node => node.Value.ValueKind == JsonValueKind.Array ? node.Value.EnumerateArray() : Enumerable.Empty<JsonElement>()))
                {
                    if (nodeResult.ValueKind == JsonValueKind.Null) continue;
                    if (nodeResult.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Check-Host.net returned an unexpected result format.");
                    if (nodeResult.TryGetProperty("time", out _))
                        // TCP got a connection and UDP only got a response so it cannot prove the port is open
                        return new(protocol, publicAddress, port, protocol == "UDP" ? "OPEN OR FILTERED" : "OPEN", null);
                    if (nodeResult.TryGetProperty("error", out var errorElement))
                    {
                        var error = errorElement.GetString() ?? "The external probe failed.";
                        if (protocol == "UDP")
                            // UDP doesnt have a handshake absent a clear ICMP refusal the result is gonna be unclear
                            return new(protocol, publicAddress, port, error.Contains("refused", StringComparison.OrdinalIgnoreCase) ? "CLOSED" : "OPEN OR FILTERED", null);
                        return new(protocol, publicAddress, port, "NOT REACHABLE", null);
                    }
                }
            }

            return new(protocol, publicAddress, port, "UNKNOWN", "The external probe timed out. Try again shortly.");
        }
        catch (Exception exception)
        {
            return new(protocol, publicAddress, port, "UNKNOWN", exception.Message);
        }
    }
}

internal sealed record PublicPortTestResult(string Protocol, string PublicAddress, int Port, string State, string? Error);
