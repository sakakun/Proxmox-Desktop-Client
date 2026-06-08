using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxDesktop.Api;
using ProxmoxDesktop.Api.Models;
using ProxmoxDesktop.Config;

namespace ProxmoxDesktop.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    // ─── Server ──────────────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string server = string.Empty;

    [ObservableProperty] private string port    = "8006";
    [ObservableProperty] private bool   skipSsl;

    // ─── Credentials ────────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string username = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string password = string.Empty;

    [ObservableProperty] private string  otp         = string.Empty;
    [ObservableProperty] private bool    totpVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private RealmData? selectedRealm;

    public ObservableCollection<RealmData> Realms { get; } = [];

    // ─── API Token ─────────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool useApiToken;

    partial void OnUseApiTokenChanged(bool value) => ErrorMessage = null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string tokenId = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string tokenSecret = string.Empty;

    // ─── State ──────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool    isBusy;
    [ObservableProperty] private string? errorMessage;

    private readonly ConfigurationService _config;
    private ApiClient? _api;
    public  IApiClient? ApiClient => _api;

    public LoginViewModel(ConfigurationService config)
    {
        _config = config;
        LoadSaved();
    }

    // ─── Commands ─────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadRealmsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Server) || string.IsNullOrWhiteSpace(Port)) return;
        IsBusy = true; ErrorMessage = null; Realms.Clear();
        try
        {
            _api = new ApiClient(Server.Trim(), Port.Trim(), SkipSsl);
            foreach (var r in await _api.GetRealmsAsync(ct)) Realms.Add(r);
            SelectedRealm = Realms.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Cannot reach server: {ex.Message}";
            _api = null;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    public async Task LoginAsync(CancellationToken ct = default)
    {
        _api ??= new ApiClient(Server.Trim(), Port.Trim(), SkipSsl);
        IsBusy = true; ErrorMessage = null;
        try
        {
            var result = UseApiToken
                ? await _api.LoginWithTokenAsync(TokenId.Trim(), TokenSecret.Trim(), ct)
                : await _api.LoginAsync(
                    Username.Trim(), Password,
                    SelectedRealm?.Realm ?? "pam",
                    TotpVisible ? Otp : null, ct);

            if      (result.IsSuccess) { SaveCredentials(); OnLoginSuccess?.Invoke(_api); }
            else if (result.NeedsTotp) TotpVisible = true;
            else                       ErrorMessage = result.Message;
        }
        catch (Exception ex) { ErrorMessage = $"Login error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private bool CanLogin()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Server)) return false;
        return UseApiToken
            ? !string.IsNullOrWhiteSpace(TokenId) && !string.IsNullOrWhiteSpace(TokenSecret)
            : !string.IsNullOrWhiteSpace(Username)
              && !string.IsNullOrWhiteSpace(Password)
              && SelectedRealm is not null;
    }

    public event Action<IApiClient>? OnLoginSuccess;

    // ─── Persistence ───────────────────────────────────────────────────────────────────

    private void LoadSaved()
    {
        var c       = _config.Config;
        Server      = c.Server;
        Port        = c.Port;
        Username    = c.Username;
        SkipSsl     = c.SkipSsl;
        UseApiToken = c.UseApiToken;
        TokenId     = c.TokenId;
    }

    private void SaveCredentials()
    {
        var c           = _config.Config;
        c.Server        = Server;
        c.Port          = Port;
        c.SkipSsl       = SkipSsl;
        c.UseApiToken   = UseApiToken;
        if (UseApiToken) c.TokenId  = TokenId;
        else             c.Username = Username;
        _config.Save();
    }
}
