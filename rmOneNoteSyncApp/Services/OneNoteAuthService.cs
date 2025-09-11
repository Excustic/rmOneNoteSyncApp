using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using rmOneNoteSyncApp.Services.Interfaces;
using AuthenticationResult = rmOneNoteSyncApp.Services.Interfaces.AuthenticationResult;

namespace rmOneNoteSyncApp.Services;

public class OneNoteAuthService : IOneNoteAuthService
{
    private readonly ILogger<OneNoteAuthService> _logger;
    private readonly IPublicClientApplication _msalClient;
    private Microsoft.Identity.Client.AuthenticationResult? _authResult;
    
    // Azure AD App Registration details
    // You need to register your app at https://portal.azure.com
    private const string ClientId = "ed7c54e7-8a64-4c3f-95fc-b045d0c0eef7";
    private const string TenantId = "consumers"; // Use "common" for multi-tenant
    private readonly string[] _scopes = new[]
    {
        "Notes.ReadWrite.All",
        "Notes.Create",
        "offline_access"
    };
    
    public event EventHandler<AuthenticationStateChangedEventArgs>? AuthenticationStateChanged;
    
    public bool IsAuthenticated => _authResult != null && _authResult.ExpiresOn > DateTimeOffset.Now;
    public string? UserName => _authResult?.Account?.Username;
    public string? AccessToken => _authResult?.AccessToken;
    public DateTime? TokenExpiry => _authResult?.ExpiresOn.DateTime;
    
    public OneNoteAuthService(ILogger<OneNoteAuthService> logger)
    {
        _logger = logger;
        
        // Configure MSAL
        var authority = $"https://login.microsoftonline.com/{TenantId}";
        
        _msalClient = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(authority)
            .WithRedirectUri("http://localhost") // For desktop apps
            .WithDefaultRedirectUri()
            .Build();
        
        // Enable token caching
        TokenCacheHelper.EnableSerialization(_msalClient.UserTokenCache);
    }
    
    public async Task<AuthenticationResult> SignInAsync()
    {
        try
        {
            _logger.LogInformation("Starting interactive sign-in flow");
            
            // First try silent authentication
            var accounts = await _msalClient.GetAccountsAsync();
            if (accounts.Any())
            {
                try
                {
                    _authResult = await _msalClient.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
                        .ExecuteAsync();
                    
                    _logger.LogInformation("Silent authentication successful for {User}", _authResult.Account.Username);
                    
                    RaiseAuthenticationStateChanged();
                    
                    return new AuthenticationResult
                    {
                        Success = true,
                        AccessToken = _authResult.AccessToken,
                        UserName = _authResult.Account.Username,
                        ExpiresOn = _authResult.ExpiresOn.DateTime
                    };
                }
                catch (MsalUiRequiredException)
                {
                    // Silent auth failed, need interactive
                    _logger.LogInformation("Silent authentication failed, starting interactive flow");
                }
            }
            
            // Interactive authentication
            _authResult = await _msalClient.AcquireTokenInteractive(_scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false) // Use system browser
                .ExecuteAsync();
            
            _logger.LogInformation("Interactive authentication successful for {User}", _authResult.Account.Username);
            
            RaiseAuthenticationStateChanged();
            
            return new AuthenticationResult
            {
                Success = true,
                AccessToken = _authResult.AccessToken,
                UserName = _authResult.Account.Username,
                ExpiresOn = _authResult.ExpiresOn.DateTime
            };
        }
        catch (MsalException msalEx)
        {
            _logger.LogError(msalEx, "MSAL authentication failed");
            return new AuthenticationResult
            {
                Success = false,
                ErrorMessage = $"Authentication failed: {msalEx.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected authentication error");
            return new AuthenticationResult
            {
                Success = false,
                ErrorMessage = $"Unexpected error: {ex.Message}"
            };
        }
    }
    
    public async Task<AuthenticationResult> SignInSilentAsync()
    {
        try
        {
            var accounts = await _msalClient.GetAccountsAsync();
            if (!accounts.Any())
            {
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "No cached accounts found"
                };
            }
            
            _authResult = await _msalClient.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
                .ExecuteAsync();
            
            RaiseAuthenticationStateChanged();
            
            return new AuthenticationResult
            {
                Success = true,
                AccessToken = _authResult.AccessToken,
                UserName = _authResult.Account.Username,
                ExpiresOn = _authResult.ExpiresOn.DateTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Silent authentication failed");
            return new AuthenticationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
    
    public async Task SignOutAsync()
    {
        try
        {
            var accounts = await _msalClient.GetAccountsAsync();
            foreach (var account in accounts)
            {
                await _msalClient.RemoveAsync(account);
            }
            
            _authResult = null;
            _logger.LogInformation("User signed out successfully");
            
            RaiseAuthenticationStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sign out");
        }
    }
    
    public async Task<string?> GetAccessTokenAsync()
    {
        try
        {
            // Check if token needs refresh
            if (_authResult == null || _authResult.ExpiresOn <= DateTimeOffset.Now.AddMinutes(5))
            {
                var accounts = await _msalClient.GetAccountsAsync();
                if (accounts.Any())
                {
                    _authResult = await _msalClient.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
                        .ExecuteAsync();
                }
                else
                {
                    return null;
                }
            }
            
            return _authResult?.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get access token");
            return null;
        }
    }
    
    private void RaiseAuthenticationStateChanged()
    {
        AuthenticationStateChanged?.Invoke(this, new AuthenticationStateChangedEventArgs
        {
            IsAuthenticated = IsAuthenticated,
            UserName = UserName
        });
    }
}

// Token cache helper for persisting tokens
public static class TokenCacheHelper
{
    private static readonly string CacheFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "rmOneNoteSyncApp",
        "msalcache.bin");
    
    private static readonly object FileLock = new object();
    
    public static void EnableSerialization(ITokenCache tokenCache)
    {
        tokenCache.SetBeforeAccess(BeforeAccessNotification);
        tokenCache.SetAfterAccess(AfterAccessNotification);
    }
    
    private static void BeforeAccessNotification(TokenCacheNotificationArgs args)
    {
        lock (FileLock)
        {
            if (System.IO.File.Exists(CacheFilePath))
            {
                args.TokenCache.DeserializeMsalV3(System.IO.File.ReadAllBytes(CacheFilePath));
            }
        }
    }
    
    private static void AfterAccessNotification(TokenCacheNotificationArgs args)
    {
        if (args.HasStateChanged)
        {
            lock (FileLock)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CacheFilePath)!);
                System.IO.File.WriteAllBytes(CacheFilePath, args.TokenCache.SerializeMsalV3());
            }
        }
    }
}