using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ProxmoxDesktop.Api.Internal;
using ProxmoxDesktop.Api.Models;

namespace ProxmoxDesktop.Api;

public sealed class ApiClient : IApiClient
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string Sentinel403 = "__403__";
    private const int    RetryCount  = 2;

    private readonly HttpClient         _http;
    private DataTicket?         _ticket;
    private ApiTokenCredential? _apiToken;
    private bool                _disposed;

    public ApiClient(ServerInfo info)
    {
        var handler = new HttpClientHandler { UseCookies = false };
        if (info.SkipSslVerification)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{info.Host}:{info.Port}/api2/json/"),
            Timeout     = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // Kept for backward compat
    public ApiClient(string server, string port, bool skipSsl)
        : this(new ServerInfo(server, int.Parse(port), skipSsl)) { }

    // ─── Auth ─────────────────────────────────────────────────────────────────────────

    public async Task<List<RealmData>> GetRealmsAsync(CancellationToken ct = default)
    {
        var body = await GetRawAsync("access/domains", ct);
        return JsonSerializer.Deserialize<PveListResponse<RealmData>>(body, _json)?.Data ?? [];
    }

    public async Task<LoginResult> LoginAsync(
        string username, string password, string realm,
        string? otp = null, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["username"] = $"{username}@{realm}",
            ["password"] = password,
            ["realm"]    = realm
        };
        if (!string.IsNullOrWhiteSpace(otp)) form["otp"] = otp;

        TicketResponse? result;
        try   { result = await PostTicketAsync(form, ct); }
        catch (Exception ex) { return LoginResult.Failure($"Cannot reach server: {ex.Message}"); }

        if (result is null) return LoginResult.Failure("Invalid server response.");

        if (result.Ticket?.Contains("PVE:!tfa!") == true)
            return string.IsNullOrWhiteSpace(otp)
                ? LoginResult.TotpRequired()
                : await TotpChallengeAsync(result.Ticket, result.Username ?? username, otp!, ct);

        if (result.Ticket is not null && result.CsrfPreventionToken is not null)
        {
            SetTicket(result, username);
            return LoginResult.Success();
        }
        return LoginResult.Failure("Invalid credentials.");
    }

    public async Task<LoginResult> LoginWithTokenAsync(
        string tokenId, string secret, CancellationToken ct = default)
    {
        tokenId = tokenId.Trim();
        secret  = secret.Trim();

        if (tokenId.StartsWith("PVEAPIToken=", StringComparison.OrdinalIgnoreCase))
        {
            var rest = tokenId["PVEAPIToken=".Length..];
            var eq   = rest.IndexOf('=');
            if (eq >= 0) { tokenId = rest[..eq]; secret = rest[(eq + 1)..]; }
        }

        if (!tokenId.Contains('@') || !tokenId.Contains('!'))
            return LoginResult.Failure("Invalid token ID format. Expected: user@realm!tokenid");
        if (string.IsNullOrWhiteSpace(secret))
            return LoginResult.Failure("Token secret cannot be empty.");

        _apiToken = new ApiTokenCredential(tokenId, secret);
        _ticket   = null;

        try
        {
            var raw = await GetAsync("nodes", ct);
            if (raw == Sentinel403) { _apiToken = null; return LoginResult.Failure("Token rejected (403 Forbidden)."); }
            return LoginResult.Success();
        }
        catch (Exception ex)
        {
            _apiToken = null;
            return LoginResult.Failure($"Cannot reach server: {ex.Message}");
        }
    }

    public async Task RenewTicketAsync(CancellationToken ct = default)
    {
        if (_apiToken is not null || _ticket is null) return;
        var form = new Dictionary<string, string>
        {
            ["username"] = _ticket.Username,
            ["password"] = _ticket.Ticket
        };
        try
        {
            var result = await PostTicketAsync(form, ct);
            if (result?.Ticket is not null && result.CsrfPreventionToken is not null)
                SetTicket(result, _ticket.Username);
        }
        catch { /* best-effort renewal — will fail on next API call and surface there */ }
    }

    // ─── Data ──────────────────────────────────────────────────────────────────────────

    public async Task<List<NodeData>> GetNodesAsync(CancellationToken ct = default)
    {
        var json = await GetAsync("nodes", ct);
        return JsonSerializer.Deserialize<PveListResponse<NodeData>>(json, _json)?.Data ?? [];
    }

    public async Task<List<MachineData>> GetAllMachinesAsync(CancellationToken ct = default)
    {
        var nodes = await GetNodesAsync(ct);
        var tasks = nodes.SelectMany(n => new[]
        {
            FetchMachinesAsync(n.Node, "qemu", ct),
            FetchMachinesAsync(n.Node, "lxc",  ct)
        });
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r)
                      .OrderBy(m => m.NodeName)
                      .ThenBy(m => m.Vmid)
                      .ToList();
    }

    public async Task<PowerResult> PowerActionAsync(
        MachineData machine, string action, bool hibernate = false,
        CancellationToken ct = default)
    {
        var type = machine.IsLxc ? "lxc" : "qemu";
        var data = new Dictionary<string, string>();
        if (hibernate && action == "suspend") data["todisk"] = "1";

        var raw = await PostAsync(
            $"nodes/{machine.NodeName}/{type}/{machine.Vmid}/status/{action}", data, ct);

        return raw switch
        {
            null          => PowerResult.Error,
            Sentinel403   => PowerResult.Forbidden,
            _             => PowerResult.Ok
        };
    }

    public async Task<string?> GetConsoleUrlAsync(
        MachineData machine, string consoleType, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var type   = machine.IsLxc ? "lxc" : "qemu";
        var server = _http.BaseAddress!.Host;
        var port   = _http.BaseAddress.Port;

        var raw = await PostAsync(
            $"nodes/{machine.NodeName}/{type}/{machine.Vmid}/vncproxy",
            new Dictionary<string, string> { ["websocket"] = "1" }, ct);

        if (raw is null or Sentinel403) return null;

        var resp = JsonSerializer.Deserialize<PveResponse<VncTicketData>>(raw, _json);
        if (resp?.Data is null) return null;

        var vncTicket = Uri.EscapeDataString(resp.Data.Ticket ?? string.Empty);
        var vmName    = Uri.EscapeDataString(machine.Name);
        var vmType    = machine.IsLxc ? "lxc" : "kvm";

        if (_apiToken is not null)
            return consoleType == "xtermjs"
                ? $"https://{server}:{port}/?console=xtermjs&vmid={machine.Vmid}&vmname={vmName}&node={machine.NodeName}&resize=1&cmd="
                : $"https://{server}:{port}/?console={vmType}&novnc=1&vmid={machine.Vmid}&vmname={vmName}&node={machine.NodeName}&resize=scale&autoconnect=1&path=api2/json/nodes/{machine.NodeName}/{type}/{machine.Vmid}/vncwebsocket&vncticket={vncTicket}";

        var authTicket = Uri.EscapeDataString(_ticket!.Ticket);
        return consoleType == "xtermjs"
            ? $"https://{server}:{port}/?console=xtermjs&vmid={machine.Vmid}&vmname={vmName}&node={machine.NodeName}&resize=1&cmd=#pve_data=token_ticket={vncTicket}"
            : $"https://{server}:{port}/?console={vmType}&novnc=1&vmid={machine.Vmid}&vmname={vmName}&node={machine.NodeName}&resize=scale&autoconnect=1&path=api2/json/nodes/{machine.NodeName}/{type}/{machine.Vmid}/vncwebsocket&vncticket={vncTicket}&PVEAuthCookie={authTicket}";
    }

    public async Task<SpiceObject?> GetSpiceConfigAsync(
        MachineData machine, string? proxy = null, CancellationToken ct = default)
    {
        if (machine.IsLxc) return null;
        var data = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(proxy)) data["proxy"] = proxy!;
        var raw = await PostAsync(
            $"nodes/{machine.NodeName}/qemu/{machine.Vmid}/spiceproxy", data, ct);
        if (raw is null or Sentinel403) return null;
        return JsonSerializer.Deserialize<PveResponse<SpiceObject>>(raw, _json)?.Data;
    }

    // ─── HTTP helpers ────────────────────────────────────────────────────────────────────

    private async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        return await ExecuteWithRetryAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            AddAuthHeaders(req);
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Forbidden) return Sentinel403;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }, ct);
    }

    private async Task<string?> PostAsync(
        string path, Dictionary<string, string>? data, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        return await ExecuteWithRetryAsync<string?>(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path);
            AddAuthHeaders(req, includeCsrf: true);
            if (data is { Count: > 0 }) req.Content = new FormUrlEncodedContent(data);
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Forbidden) return Sentinel403;
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }, ct);
    }

    private async Task<string> GetRawAsync(string path, CancellationToken ct)
    {
        var resp = await _http.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<TicketResponse?> PostTicketAsync(
        Dictionary<string, string> form, CancellationToken ct)
    {
        var resp = await _http.PostAsync(
            "access/ticket", new FormUrlEncodedContent(form), ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<PveResponse<TicketResponse>>(body, _json)?.Data;
    }

    private async Task<List<MachineData>> FetchMachinesAsync(
        string node, string type, CancellationToken ct)
    {
        try
        {
            var json = await GetAsync($"nodes/{node}/{type}", ct);
            if (json == Sentinel403) return [];
            return JsonSerializer
                .Deserialize<PveListResponse<MachineData>>(json, _json)?
                .Data?
                .Select(m => m with { NodeName = node, Type = type })
                .ToList() ?? [];
        }
        catch { return []; }
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> action, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try   { return await action(); }
            catch (HttpRequestException) when (++attempt < RetryCount)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    private void AddAuthHeaders(HttpRequestMessage req, bool includeCsrf = false)
    {
        if (_apiToken is not null)
            req.Headers.TryAddWithoutValidation(
                "Authorization", $"PVEAPIToken={_apiToken.TokenId}={_apiToken.Secret}");
        else
        {
            req.Headers.TryAddWithoutValidation("Cookie", $"PVEAuthCookie={_ticket!.Ticket}");
            if (includeCsrf)
                req.Headers.TryAddWithoutValidation(
                    "CSRFPreventionToken", _ticket.CsrfPreventionToken);
        }
    }

    private void SetTicket(TicketResponse result, string fallbackUsername)
    {
        _ticket = new DataTicket
        {
            Ticket              = result.Ticket!,
            CsrfPreventionToken = result.CsrfPreventionToken!,
            Username            = result.Username ?? fallbackUsername
        };
        _apiToken = null;
    }

    private void EnsureAuthenticated()
    {
        if (_ticket is null && _apiToken is null)
            throw new InvalidOperationException("Not authenticated. Call LoginAsync first.");
    }

    private async Task<LoginResult> TotpChallengeAsync(
        string tfaTicket, string username, string otp, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["username"]      = username,
            ["password"]      = $"totp:{otp}",
            ["tfa-challenge"] = tfaTicket
        };
        TicketResponse? result;
        try   { result = await PostTicketAsync(form, ct); }
        catch (Exception ex) { return LoginResult.Failure($"TOTP error: {ex.Message}"); }
        if (result?.Ticket is null || result.CsrfPreventionToken is null)
            return LoginResult.Failure("Invalid TOTP code.");
        SetTicket(result, username);
        return LoginResult.Success();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _http.Dispose();
        _disposed = true;
    }

    private sealed class VncTicketData
    {
        [System.Text.Json.Serialization.JsonPropertyName("ticket")]
        public string? Ticket { get; init; }
    }
}
