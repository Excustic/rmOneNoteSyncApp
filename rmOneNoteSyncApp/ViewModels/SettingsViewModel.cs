using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IDatabaseService _databaseService;
    private readonly ISshService _sshService;
    private readonly ILogger<SettingsViewModel>? _logger;
    private SyncConfiguration? _configuration;
    
    [ObservableProperty]
    private bool _enableWifiSync;
    
    [ObservableProperty]
    private int _syncIntervalMinutes;
    
    [ObservableProperty]
    private bool _autoSync;
    
    [ObservableProperty]
    private string _targetNotebook;
    
    [ObservableProperty]
    private long _maxCacheSizeMB;
    
    [ObservableProperty]
    private int _cacheRetentionDays;
    
    [ObservableProperty]
    private bool _keepLocalCopies;
    
    [ObservableProperty]
    private bool _isDeviceConnected;
    
     [ObservableProperty]
    private bool _isRestartingService;
    
    [ObservableProperty]
    private string _serviceStatus = "Unknown";
    
    [ObservableProperty]
    private string _deviceInfo = "No device connected";
    
    public SettingsViewModel(IDatabaseService databaseService, ISshService sshService)
    {
        _databaseService = databaseService;
        _sshService = sshService;
        
        try
        {
            _logger = App.ServiceProvider?.GetService<ILogger<SettingsViewModel>>();
        }
        catch { }
        
        _sshService.OnConnectionChanged += SshServiceOnOnConnectionChanged;
        SshServiceOnOnConnectionChanged(this, _sshService.IsConnected);
        // Load configuration
        Task.Run(LoadSettingsAsync);
    }

    private void SshServiceOnOnConnectionChanged(object? sender, bool e)
    {
        if (e)
        {
            Task.Run(async() =>
            {
                var info = await _sshService.GetDeviceInfoAsync();
                DeviceInfo = $"Connected to {info.GetValueOrDefault("Model", "reMarkable")}";
            });
        }
        else
        {
            DeviceInfo = "No device connected.";
            ServiceStatus = "N/A";
        }
        
        IsDeviceConnected = e;
        OnPropertyChanged(nameof(IsDeviceConnected));
        OnPropertyChanged(nameof(ServiceStatus));
        OnPropertyChanged(nameof(DeviceInfo));
        DisconnectDeviceCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadSettingsAsync()
    {
        var config = await _databaseService.GetConfigurationAsync();
        if (config != null)
        {
            _configuration = config;
            EnableWifiSync = config.EnableWifiSync;
            SyncIntervalMinutes = config.SyncIntervalMinutes;
            AutoSync = config.AutoSync;
            TargetNotebook = config.TargetNotebook;
            MaxCacheSizeMB = config.MaxCacheSizeMB;
            CacheRetentionDays = config.CacheRetentionDays;
            KeepLocalCopies = config.KeepLocalCopies;
        }
    }
    
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _configuration.EnableWifiSync = EnableWifiSync;
        _configuration.SyncIntervalMinutes = SyncIntervalMinutes;
        _configuration.AutoSync = AutoSync;
        _configuration.TargetNotebook = TargetNotebook;
        _configuration.MaxCacheSizeMB = MaxCacheSizeMB;
        _configuration.CacheRetentionDays = CacheRetentionDays;
        _configuration.KeepLocalCopies = KeepLocalCopies;
        
        await _databaseService.SaveConfigurationAsync(_configuration);
        _logger?.LogInformation("Settings saved");
    }
    
    [RelayCommand]
    private async Task DisconnectDeviceAsync()
    {
        try
        {
            _logger?.LogInformation("Disconnecting device and resetting configuration");
            
            // Disconnect SSH
            if (_sshService.IsConnected)
            {
                await _sshService.DisconnectAsync();
            }
            
            // Clear the configuration to force setup screen
            await _databaseService.ClearCacheAsync();
            
            // Clear saved configuration
            var config = await _databaseService.GetConfigurationAsync();
            if (config != null)
            {
                config.DeviceIp = string.Empty;
                config.DevicePassword = string.Empty;
                config.ServiceVersion = string.Empty;
                await _databaseService.SaveConfigurationAsync(config);
            }
            
            _logger?.LogInformation("Device disconnected and configuration reset");
            
            // Navigate back to setup
            if (App.ServiceProvider?.GetService<MainViewModel>() is { } mainVm)
            {
                mainVm.ShowSetupScreen = true;
                mainVm.ConnectionState = ConnectionState.Disconnected;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to disconnect device");
        }
    }
    
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await _databaseService.ClearCacheAsync();
        _logger?.LogInformation("Cache cleared");
    }
    
    [RelayCommand]
    private async Task CleanupOldCacheAsync()
    {
        var deleted = await _databaseService.CleanupOldCacheAsync(CacheRetentionDays);
        _logger?.LogInformation("Cleaned up {Count} old cache entries", deleted);
    }
    
    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        try
        {
            IsRestartingService = true;
            ServiceStatus = "Restarting...";
            
            _logger?.LogInformation("Restarting reMarkable sync services");
            
            // Stop the services first
            await _sshService.ExecuteCommandAsync("systemctl stop onenote-sync-watcher");
            await _sshService.ExecuteCommandAsync("systemctl stop onenote-sync-httpclient");
            
            // Wait a moment for services to fully stop
            await Task.Delay(2000);
            
            // Start the services again
            await _sshService.ExecuteCommandAsync("systemctl start onenote-sync-watcher");
            await _sshService.ExecuteCommandAsync("systemctl start onenote-sync-httpclient");
            
            // Wait for services to start
            await Task.Delay(2000);
            
            // Check service status
            var watcherStatus = await _sshService.CheckServiceStatusAsync("onenote-sync-watcher");
            var httpClientStatus = await _sshService.CheckServiceStatusAsync("onenote-sync-httpclient");
            
            if (watcherStatus && httpClientStatus)
            {
                ServiceStatus = "Services running";
                _logger?.LogInformation("Services restarted successfully");
            }
            else
            {
                ServiceStatus = "Service error";
                _logger?.LogWarning("Services may not have started correctly");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to restart services");
            ServiceStatus = "Restart failed";
        }
        finally
        {
            IsRestartingService = false;
        }
    }
    
    [RelayCommand]
    private async Task CheckServiceStatusAsync()
    {
        try
        {
            if (!_sshService.IsConnected)
            {
                ServiceStatus = "Not connected";
                return;
            }
            
            var watcherStatus = await _sshService.CheckServiceStatusAsync("onenote-sync-watcher");
            var httpClientStatus = await _sshService.CheckServiceStatusAsync("onenote-sync-httpclient");
            
            if (watcherStatus && httpClientStatus)
            {
                ServiceStatus = "All services running";
            }
            else if (watcherStatus || httpClientStatus)
            {
                ServiceStatus = "Some services running";
            }
            else
            {
                ServiceStatus = "Services stopped";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to check service status");
            ServiceStatus = "Unknown";
        }
    }
    
    // Check status when connecting
    partial void OnIsDeviceConnectedChanged(bool value)
    {
        if (value)
        {
            Task.Run(CheckServiceStatusAsync);
        }
    }
}