using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class SyncService : ISyncService
{
    private readonly ILogger<SyncService> _logger;
    private readonly ISshService _sshService;
    private readonly IDatabaseService _databaseService;
    private readonly Timer? _autoSyncTimer;
    private bool _isSyncing;
    private CancellationTokenSource? _autoSyncCancellation;
    
    public event EventHandler<SyncProgressEventArgs>? SyncProgress;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;
    
    public bool IsSyncing => _isSyncing;
    public DateTime? LastSyncTime { get; private set; }
    
    public SyncService(
        ILogger<SyncService> logger,
        ISshService sshService,
        IDatabaseService databaseService)
    {
        _logger = logger;
        _sshService = sshService;
        _databaseService = databaseService;
    }
    
    public async Task<SyncResult> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        if (_isSyncing)
        {
            throw new InvalidOperationException("Sync already in progress");
        }
        
        _isSyncing = true;
        var result = new SyncResult { StartTime = DateTime.UtcNow };
        var startTime = DateTime.Now;
        
        try
        {
            ReportProgress("Starting sync...", 0, 0);
            
            // Get all pending pages
            var pendingPages = await _databaseService.GetPendingPagesAsync(1000);
            result.TotalDocuments = pendingPages.GroupBy(p => p.DocumentId).Count();
            
            ReportProgress($"Found {pendingPages.Count} pages to sync", pendingPages.Count, 0);
            
            int processed = 0;
            var errors = new List<string>();
            
            foreach (var page in pendingPages)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                
                try
                {
                    ReportProgress($"Syncing {page.Title}", pendingPages.Count, processed);
                    
                    // This is where we would:
                    // 1. Download the .rm file from reMarkable
                    // 2. Convert to InkML
                    // 3. Upload to OneNote
                    // For now, just simulate
                    await Task.Delay(100, cancellationToken);
                    
                    await _databaseService.UpdatePageStatusAsync(
                        page.DocumentId, 
                        page.PageId, 
                        SyncStatus.Uploaded);
                    
                    result.SuccessfulDocuments++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sync page {PageId}", page.PageId);
                    errors.Add($"{page.Title}: {ex.Message}");
                    
                    await _databaseService.UpdatePageStatusAsync(
                        page.DocumentId,
                        page.PageId,
                        SyncStatus.Failed,
                        ex.Message);
                    
                    result.FailedDocuments++;
                }
                
                processed++;
            }
            
            result.Success = result.FailedDocuments == 0;
            result.Errors = errors;
            LastSyncTime = DateTime.Now;
            
            var duration = DateTime.Now - startTime;
            ReportCompleted(result.Success, result.SuccessfulDocuments, result.FailedDocuments, duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            result.Success = false;
            result.Errors.Add(ex.Message);
            ReportCompleted(false, 0, 0, DateTime.Now - startTime, ex.Message);
        }
        finally
        {
            _isSyncing = false;
            result.EndTime = DateTime.UtcNow;
        }
        
        return result;
    }
    
    public async Task<SyncResult> SyncDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var document = await _databaseService.GetDocumentMetadataAsync(documentId);
        if (document == null)
        {
            throw new ArgumentException($"Document {documentId} not found");
        }
        
        // Sync just this document's pages
        var result = new SyncResult { StartTime = DateTime.UtcNow };
        // Implementation would be similar to SyncAllAsync but filtered
        return result;
    }
    
    public async Task<SyncResult> SyncFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        // Get all documents in folder and sync them
        var documents = await _databaseService.GetAllDocumentsAsync();
        var folderDocs = documents.Where(d => d.Parent == folderPath).ToList();
        
        var result = new SyncResult { StartTime = DateTime.UtcNow };
        // Implementation would sync all documents in the folder
        return result;
    }
    
    public async Task StartAutomaticSyncAsync(int intervalMinutes)
    {
        _autoSyncCancellation = new CancellationTokenSource();
        
        while (!_autoSyncCancellation.Token.IsCancellationRequested)
        {
            try
            {
                await SyncAllAsync(_autoSyncCancellation.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-sync failed");
            }
            
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), _autoSyncCancellation.Token);
        }
    }
    
    public async Task StopAutomaticSyncAsync()
    {
        _autoSyncCancellation?.Cancel();
        
        // Wait for any ongoing sync to complete
        while (_isSyncing)
        {
            await Task.Delay(100);
        }
    }
    
    private void ReportProgress(string message, int total, int processed)
    {
        SyncProgress?.Invoke(this, new SyncProgressEventArgs
        {
            Message = message,
            TotalItems = total,
            ProcessedItems = processed
        });
    }
    
    private void ReportCompleted(bool success, int synced, int failed, TimeSpan duration, string? error = null)
    {
        SyncCompleted?.Invoke(this, new SyncCompletedEventArgs
        {
            Success = success,
            ItemsSynced = synced,
            ItemsFailed = failed,
            Duration = duration,
            ErrorMessage = error
        });
    }
    
    public void Dispose()
    {
        _autoSyncCancellation?.Cancel();
        _autoSyncTimer?.Dispose();
    }
}