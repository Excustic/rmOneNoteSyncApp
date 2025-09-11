using System;
using System.Collections.Generic;

namespace rmOneNoteSyncApp.Models;

/// <summary>
/// Configuration model for sync settings
/// </summary>
public class SyncConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Connection settings
    public string DeviceIp { get; set; } = "10.11.99.1";
    public bool EnableWifiSync { get; set; } = true;
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public string DevicePassword { get; set; }
    
    // Sync settings
    public List<string> SyncFiles { get; set; } = [];
    public int SyncIntervalMinutes { get; set; } = 30;
    public bool AutoSync { get; set; } = true;
    
    // OneNote settings
    public string? OneNoteAccessToken { get; set; }
    public string? OneNoteRefreshToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
    public string TargetNotebook { get; set; } = "reMarkable Sync";
    public string TargetSection { get; set; } = "Documents";
    
    // Service settings
    public string ServiceVersion { get; set; } = "1.0.0";
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;
    
    // Storage settings
    public string LocalCachePath { get; set; } = "";
    public long MaxCacheSizeMB { get; set; } = 500;
    public bool KeepLocalCopies { get; set; } = true;
    public int CacheRetentionDays { get; set; } = 30;
}