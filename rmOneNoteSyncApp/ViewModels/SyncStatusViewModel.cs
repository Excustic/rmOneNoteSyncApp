using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.ViewModels;

public partial class SyncStatusViewModel : ViewModelBase
{
    private readonly ILogger<SyncStatusViewModel>? _logger;
    private readonly IDatabaseService _databaseService;
    private readonly ISyncServerService _syncServer;
    private readonly IOneNoteClient _oneNoteClient;
    private readonly ISyncService _syncService;
    
    [ObservableProperty]
    private ObservableCollection<SyncItem> _syncItems = new();
    
    [ObservableProperty]
    private ObservableCollection<SyncQueueItem> _queueItems = new();
    
    [ObservableProperty]
    private bool _isServerRunning;
    
    [ObservableProperty]
    private string _serverStatus = "Server stopped";
    
    [ObservableProperty]
    private int _totalReceived;
    
    [ObservableProperty]
    private int _totalUploaded;
    
    [ObservableProperty]
    private int _totalPending;
    
    [ObservableProperty]
    private int _totalFailed;
    
    [ObservableProperty]
    private bool _isSyncing;
    
    [ObservableProperty]
    private string _syncProgress = "";
    
    public SyncStatusViewModel(
        IDatabaseService databaseService,
        ISyncServerService syncServer,
        IOneNoteClient oneNoteClient,
        ISyncService syncService)
    {
        _databaseService = databaseService;
        _syncServer = syncServer;
        _oneNoteClient = oneNoteClient;
        _syncService = syncService;
        
        try
        {
            _logger = App.ServiceProvider?.GetService<ILogger<SyncStatusViewModel>>();
        }
        catch { }
        
        // Subscribe to server events
        _syncServer.FileReceived += OnFileReceived;
        
        // Subscribe to sync events
        _syncService.SyncProgress += OnSyncProgress;
        _syncService.SyncCompleted += OnSyncCompleted;
        
        // Load initial data
        Task.Run(LoadSyncStatusAsync);
        
        // Start server if not running
        if (!_syncServer.IsRunning)
        {
            Task.Run(StartServerAsync);
        }
    }
    
    private async Task LoadSyncStatusAsync()
    {
        try
        {
            // Load recent sync items from database
            var recentPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Pending);
            var uploadedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Uploaded);
            var failedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Failed);
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                SyncItems.Clear();
                
                // Add recent items
                foreach (var page in recentPages.Take(50))
                {
                    SyncItems.Add(MapToSyncItem(page));
                }
                
                foreach (var page in uploadedPages.Take(20))
                {
                    SyncItems.Add(MapToSyncItem(page));
                }
                
                // Update statistics
                TotalPending = recentPages.Count;
                TotalUploaded = uploadedPages.Count;
                TotalFailed = failedPages.Count;
                TotalReceived = TotalPending + TotalUploaded + TotalFailed;
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load sync status");
        }
    }
    
    private SyncItem MapToSyncItem(PageMetadata page)
    {
        var (notebook, section, pageName) = ParseVirtualPath(page.VirtualPath);
        
        return new SyncItem
        {
            DocumentId = page.DocumentId,
            PageId = page.PageId,
            FileName = Path.GetFileName(page.LocalFilePath),
            VirtualPath = page.VirtualPath,
            Notebook = notebook,
            Section = section,
            PageName = pageName,
            FileSize = FormatFileSize(page.FileSizeBytes),
            ReceivedTime = page.LastModified,
            Status = page.Status,
            LastError = page.LastError,
            OneNoteUrl = page.OneNotePageUrl
        };
    }
    
    private void OnFileReceived(object? sender, FileReceivedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var (notebook, section, pageName) = ParseVirtualPath(e.VirtualPath);
            
            var item = new SyncItem
            {
                DocumentId = e.DocumentId,
                PageId = e.PageId,
                FileName = Path.GetFileName(e.LocalPath),
                VirtualPath = e.VirtualPath,
                Notebook = notebook,
                Section = section,
                PageName = pageName,
                FileSize = FormatFileSize(e.FileSize),
                ReceivedTime = e.ReceivedAt,
                Status = SyncStatus.Pending
            };
            
            // Add to beginning of list
            SyncItems.Insert(0, item);
            
            // Update counters
            TotalReceived++;
            TotalPending++;
            
            _logger?.LogInformation("File received: {Path}", e.VirtualPath);
            
            // Auto-start sync if enabled
            if (!_isSyncing)
            {
                Task.Run(ProcessQueueAsync);
            }
        });
    }
    
    private void OnSyncProgress(object? sender, SyncProgressEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SyncProgress = e.Message;
        });
    }
    
    private void OnSyncCompleted(object? sender, SyncCompletedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (e.Success)
            {
                SyncProgress = $"Sync completed: {e.ItemsSynced} items uploaded";
            }
            else
            {
                SyncProgress = $"Sync failed: {e.ErrorMessage}";
            }
            
            // Reload items
            Task.Run(LoadSyncStatusAsync);
        });
    }
    
    [RelayCommand]
    private async Task StartServerAsync()
    {
        try
        {
            if (!_syncServer.IsRunning)
            {
                await _syncServer.StartAsync();
                IsServerRunning = true;
                ServerStatus = $"Server running on port 8080";
                _logger?.LogInformation("Sync server started");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start server");
            ServerStatus = $"Server error: {ex.Message}";
        }
    }
    
    [RelayCommand]
    private async Task StopServerAsync()
    {
        try
        {
            if (_syncServer.IsRunning)
            {
                await _syncServer.StopAsync();
                IsServerRunning = false;
                ServerStatus = "Server stopped";
                _logger?.LogInformation("Sync server stopped");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to stop server");
        }
    }
    
    [RelayCommand]
    private async Task ProcessQueueAsync()
    {
        if (IsSyncing)
            return;
        
        try
        {
            IsSyncing = true;
            SyncProgress = "Processing sync queue...";
            
            // Get pending pages
            var pendingPages = await _databaseService.GetPendingPagesAsync(10);
            
            foreach (var page in pendingPages)
            {
                try
                {
                    SyncProgress = $"Uploading {page.Title}...";
                    
                    // Parse path structure
                    var (notebook, section, pageName) = ParseVirtualPath(page.VirtualPath);
                    
                    // Ensure notebook exists
                    var notebooks = await _oneNoteClient.GetNotebooksAsync();
                    var targetNotebook = notebooks.FirstOrDefault(n => n.DisplayName == notebook);
                    
                    if (targetNotebook == null)
                    {
                        targetNotebook = await _oneNoteClient.CreateNotebookAsync(notebook);
                    }
                    
                    // Ensure section exists
                    var sections = await _oneNoteClient.GetSectionsAsync(targetNotebook.Id!);
                    var targetSection = sections.FirstOrDefault(s => s.DisplayName == section);
                    
                    if (targetSection == null)
                    {
                        targetSection = await _oneNoteClient.CreateSectionAsync(targetNotebook.Id!, section);
                    }
                    
                    // Read .rm file
                    var rmData = await File.ReadAllBytesAsync(page.LocalFilePath);
                    
                    // Upload to OneNote as InkML
                    var metadata = new Dictionary<string, string>
                    {
                        ["Original Path"] = page.VirtualPath,
                        ["Document ID"] = page.DocumentId,
                        ["Page Number"] = page.PageNumber,
                        ["Imported"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                    
                    var oneNotePageId = await _oneNoteClient.UploadInkMLPageAsync(
                        targetSection.Id!,
                        pageName,
                        rmData,
                        metadata);
                    
                    // Update database
                    await _databaseService.UpdatePageStatusAsync(
                        page.DocumentId,
                        page.PageId,
                        SyncStatus.Uploaded);
                    
                    // Update UI
                    var syncItem = SyncItems.FirstOrDefault(i => 
                        i.DocumentId == page.DocumentId && i.PageId == page.PageId);
                    
                    if (syncItem != null)
                    {
                        syncItem.Status = SyncStatus.Uploaded;
                        syncItem.OneNoteUrl = $"onenote:///pages/{oneNotePageId}";
                    }
                    
                    TotalUploaded++;
                    TotalPending--;
                    
                    _logger?.LogInformation("Successfully uploaded {Page} to OneNote", pageName);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to upload page {Page}", page.Title);
                    
                    await _databaseService.UpdatePageStatusAsync(
                        page.DocumentId,
                        page.PageId,
                        SyncStatus.Failed,
                        ex.Message);
                    
                    TotalFailed++;
                    TotalPending--;
                }
            }
            
            SyncProgress = "Queue processing complete";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Queue processing failed");
            SyncProgress = $"Error: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }
    
    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        // Reset failed items to pending
        var failedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Failed);
        
        foreach (var page in failedPages)
        {
            await _databaseService.UpdatePageStatusAsync(
                page.DocumentId,
                page.PageId,
                SyncStatus.Pending);
        }
        
        TotalPending += failedPages.Count;
        TotalFailed = 0;
        
        // Reload and process
        await LoadSyncStatusAsync();
        await ProcessQueueAsync();
    }
    
    [RelayCommand]
    private async Task ClearCompletedAsync()
    {
        // Clear uploaded items from view
        SyncItems.Clear();
        
        // Reload only pending and failed
        await LoadSyncStatusAsync();
    }
    
    private (string notebook, string section, string page) ParseVirtualPath(string virtualPath)
    {
        var parts = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length == 0)
            return ("rm_Uncategorized", "Default", "Untitled");
        
        if (parts.Length == 1)
            return ("rm_Uncategorized", "Default", parts[0]);
        
        if (parts.Length == 2)
            return ($"rm_{parts[0]}", parts[0], parts[1]);
        
        var notebookParts = parts.Take(parts.Length - 2).ToList();
        var section = parts[parts.Length - 2];
        var page = parts[parts.Length - 1];
        
        var notebook = "rm_" + string.Join("_", notebookParts);
        
        return (notebook, section, page);
    }
    
    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        
        return $"{size:0.##} {sizes[order]}";
    }
}

public class SyncItem : ObservableObject
{
    public string DocumentId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string VirtualPath { get; set; } = "";
    public string Notebook { get; set; } = "";
    public string Section { get; set; } = "";
    public string PageName { get; set; } = "";
    public string FileSize { get; set; } = "";
    public DateTime ReceivedTime { get; set; }
    
    private SyncStatus _status;
    public SyncStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
    
    public string? LastError { get; set; }
    public string? OneNoteUrl { get; set; }
    
    public string StatusDisplay => Status switch
    {
        SyncStatus.Pending => "⏳ Pending",
        SyncStatus.InProgress => "🔄 Uploading...",
        SyncStatus.Uploaded => "✅ Uploaded",
        SyncStatus.Failed => "❌ Failed",
        _ => "Unknown"
    };
}

public class SyncQueueItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public double Progress { get; set; }
}