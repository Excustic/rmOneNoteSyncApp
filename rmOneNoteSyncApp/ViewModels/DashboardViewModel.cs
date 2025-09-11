using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly IDatabaseService _databaseService;
    private readonly ISshService _sshService;
    
    [ObservableProperty]
    private bool _isConnected;
    
    [ObservableProperty]
    private string _deviceStatus = "Disconnected";
    
    [ObservableProperty]
    private string _deviceIp = "--";
    
    [ObservableProperty]
    private string _lastConnected = "Never";
    
    [ObservableProperty]
    private string _wifiStatus = "Unknown";
    
    [ObservableProperty]
    private string _storageInfo = "-- / --";
    
    [ObservableProperty]
    private int _totalDocuments;
    
    [ObservableProperty]
    private int _totalPages;
    
    [ObservableProperty]
    private int _syncedDocuments;
    
    [ObservableProperty]
    private int _pendingDocuments;
    
    [ObservableProperty]
    private string _lastSyncTime = "Never";
    
    [ObservableProperty]
    private ObservableCollection<ActivityItem> _recentActivities = new();
    
    public DashboardViewModel(IDatabaseService databaseService, ISshService sshService)
    {
        _databaseService = databaseService;
        _sshService = sshService;
        _sshService.OnConnectionChanged += async(sender, b) => await LoadDashboardDataAsync(); 
        // Load initial data
        Task.Run(LoadDashboardDataAsync);
    }
    
    private async Task LoadDashboardDataAsync()
    {
        // Load statistics
        var documents = await _databaseService.GetAllDocumentsAsync();
        TotalDocuments = documents.Count;
        TotalPages = 0;
        foreach (var doc in documents)
        {
            TotalPages += doc.Pages.Count;
        }
        
        // Count synced vs pending
        var pendingPages = await _databaseService.GetPendingPagesAsync();
        PendingDocuments = pendingPages.Count;
        
        var uploadedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Uploaded);
        SyncedDocuments = uploadedPages.Count;
        
        // Load recent activity
        var syncHistory = await _databaseService.GetSyncHistoryAsync(10);
        foreach (var evt in syncHistory)
        {
            RecentActivities.Add(new ActivityItem
            {
                Timestamp = evt.Timestamp,
                DocumentName = evt.DocumentId,
                Action = evt.Success ? "Synced" : "Failed",
                Status = evt.Success ? "Success" : "Error"
            });
        }
        
        // Check device connection
        if (_sshService.IsConnected)
        {
            IsConnected = true;
            DeviceStatus = "Connected";
            
            try
            {
                var deviceInfo = await _sshService.GetDeviceInfoAsync();
                if (deviceInfo.TryGetValue("StorageUsed", out var used) && 
                    deviceInfo.TryGetValue("StorageAvailable", out var avail))
                {
                    StorageInfo = $"{used} / {avail}";
                }
            }
            catch
            {
                // Ignore errors
            }
        }
    }
    
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        // TODO: Trigger sync
        await Task.Delay(1000);
        LastSyncTime = DateTime.Now.ToString("HH:mm:ss");
    }
}

public class ActivityItem
{
    public DateTime Timestamp { get; set; }
    public string DocumentName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "";
}