using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using rmOneNoteSyncApp.Models;

namespace rmOneNoteSyncApp.Services.Interfaces;

/// <summary>
/// Main synchronization service
/// </summary>
public interface ISyncService : IDisposable
{
    event EventHandler<SyncProgressEventArgs>? SyncProgress;
    event EventHandler<SyncCompletedEventArgs>? SyncCompleted;
    
    bool IsSyncing { get; }
    DateTime? LastSyncTime { get; }
    
    Task<SyncResult> SyncAllAsync(CancellationToken cancellationToken = default);
    Task<SyncResult> SyncDocumentAsync(string documentId, CancellationToken cancellationToken = default);
    Task<SyncResult> SyncFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    
    Task StartAutomaticSyncAsync(int intervalMinutes);
    Task StopAutomaticSyncAsync();
}

public class SyncProgressEventArgs : EventArgs
{
    public string Message { get; set; } = "";
    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public string? CurrentItem { get; set; }
    public double ProgressPercentage => TotalItems > 0 ? (ProcessedItems * 100.0 / TotalItems) : 0;
}

public class SyncCompletedEventArgs : EventArgs
{
    public bool Success { get; set; }
    public int ItemsSynced { get; set; }
    public int ItemsFailed { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SyncResult
{
    public bool Success { get; set; }
    public int TotalDocuments { get; set; }
    public int SuccessfulDocuments { get; set; }
    public int FailedDocuments { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
}