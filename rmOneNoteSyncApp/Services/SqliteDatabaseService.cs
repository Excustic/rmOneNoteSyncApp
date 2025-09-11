using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class SqliteDatabaseService : IDatabaseService
{
    private readonly ILogger<SqliteDatabaseService> _logger;
    private SqliteConnection? _connection;
    private string? _databasePath;
    
    public SqliteDatabaseService(ILogger<SqliteDatabaseService> logger)
    {
        _logger = logger;
    }
    
    public async Task InitializeAsync(string databasePath)
    {
        _databasePath = databasePath;
        
        // Ensure directory exists
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        
        // Create connection
        _connection = new SqliteConnection($"Data Source={databasePath}");
        await _connection.OpenAsync();
        
        // Create tables
        await CreateTablesAsync();
        
        _logger.LogInformation("Database initialized at {Path}", databasePath);
    }
    
    private async Task CreateTablesAsync()
    {
        var createTablesSql = @"
            CREATE TABLE IF NOT EXISTS Configuration (
                Id TEXT PRIMARY KEY,
                Json TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            
            CREATE TABLE IF NOT EXISTS Documents (
                DocumentId TEXT PRIMARY KEY,
                VisibleName TEXT NOT NULL,
                Type TEXT NOT NULL,
                Parent TEXT,
                LastModified TEXT NOT NULL,
                Json TEXT
            );
            
            CREATE TABLE IF NOT EXISTS Pages (
                DocumentId TEXT NOT NULL,
                PageId TEXT NOT NULL,
                PageNumber TEXT,
                Title TEXT,
                VirtualPath TEXT,
                LocalFilePath TEXT,
                CachedFilePath TEXT,
                FileSizeBytes INTEGER,
                LastModified TEXT NOT NULL,
                ContentHash TEXT,
                Status INTEGER NOT NULL,
                LastSyncTime TEXT,
                OneNotePageId TEXT,
                OneNotePageUrl TEXT,
                RetryCount INTEGER DEFAULT 0,
                LastError TEXT,
                Json TEXT,
                PRIMARY KEY (DocumentId, PageId),
                FOREIGN KEY (DocumentId) REFERENCES Documents(DocumentId)
            );
            
            CREATE TABLE IF NOT EXISTS SyncHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                DocumentId TEXT NOT NULL,
                PageId TEXT NOT NULL,
                Success INTEGER NOT NULL,
                Details TEXT
            );
            
            CREATE INDEX IF NOT EXISTS idx_pages_status ON Pages(Status);
            CREATE INDEX IF NOT EXISTS idx_pages_lastsync ON Pages(LastSyncTime);
            CREATE INDEX IF NOT EXISTS idx_synchistory_timestamp ON SyncHistory(Timestamp);";
        
        await _connection!.ExecuteAsync(createTablesSql);
    }
    
    public async Task<SyncConfiguration?> GetConfigurationAsync()
    {
        var sql = "SELECT Json FROM Configuration ORDER BY UpdatedAt DESC LIMIT 1";
        var json = await _connection!.QueryFirstOrDefaultAsync<string>(sql);
        
        if (string.IsNullOrEmpty(json))
            return null;
        
        return System.Text.Json.JsonSerializer.Deserialize<SyncConfiguration>(json);
    }
    
    public async Task SaveConfigurationAsync(SyncConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        var json = System.Text.Json.JsonSerializer.Serialize(config);
        
        var sql = @"
            INSERT OR REPLACE INTO Configuration (Id, Json, UpdatedAt)
            VALUES (@Id, @Json, @UpdatedAt)";
        
        await _connection!.ExecuteAsync(sql, new
        {
            config.Id,
            Json = json,
            UpdatedAt = config.UpdatedAt.ToString("O")
        });
    }
    
    public async Task<PageMetadata?> GetPageMetadataAsync(string documentId, string pageId)
    {
        var sql = @"
            SELECT * FROM Pages 
            WHERE DocumentId = @DocumentId AND PageId = @PageId";
        
        var result = await _connection!.QueryFirstOrDefaultAsync<dynamic>(sql, new { documentId, pageId });
        
        if (result == null)
            return null;
        
        return MapToPageMetadata(result);
    }
    
    public async Task<List<PageMetadata>> GetPendingPagesAsync(int limit = 100)
    {
        var sql = @"
            SELECT * FROM Pages 
            WHERE Status = @Status 
            ORDER BY LastModified DESC 
            LIMIT @Limit";
        
        var results = await _connection!.QueryAsync<dynamic>(sql, new 
        { 
            Status = (int)SyncStatus.Pending,
            Limit = limit
        });
        
        return results.Select(MapToPageMetadata).ToList();
    }
    
    public async Task<List<PageMetadata>> GetPagesByStatusAsync(SyncStatus status)
    {
        var sql = "SELECT * FROM Pages WHERE Status = @Status";
        var results = await _connection!.QueryAsync<dynamic>(sql, new { Status = (int)status });
        return results.Select(MapToPageMetadata).ToList();
    }
    
    public async Task SavePageMetadataAsync(PageMetadata metadata)
    {
        var sql = @"
            INSERT OR REPLACE INTO Pages (
                DocumentId, PageId, PageNumber, Title, VirtualPath,
                LocalFilePath, CachedFilePath, FileSizeBytes, LastModified,
                ContentHash, Status, LastSyncTime, OneNotePageId, OneNotePageUrl,
                RetryCount, LastError, Json
            ) VALUES (
                @DocumentId, @PageId, @PageNumber, @Title, @VirtualPath,
                @LocalFilePath, @CachedFilePath, @FileSizeBytes, @LastModified,
                @ContentHash, @Status, @LastSyncTime, @OneNotePageId, @OneNotePageUrl,
                @RetryCount, @LastError, @Json
            )";
        
        await _connection!.ExecuteAsync(sql, new
        {
            metadata.DocumentId,
            metadata.PageId,
            metadata.PageNumber,
            metadata.Title,
            metadata.VirtualPath,
            metadata.LocalFilePath,
            metadata.CachedFilePath,
            metadata.FileSizeBytes,
            LastModified = metadata.LastModified.ToString("O"),
            metadata.ContentHash,
            Status = (int)metadata.Status,
            LastSyncTime = metadata.LastSyncTime?.ToString("O"),
            metadata.OneNotePageId,
            metadata.OneNotePageUrl,
            metadata.RetryCount,
            metadata.LastError,
            Json = System.Text.Json.JsonSerializer.Serialize(metadata)
        });
    }
    
    public async Task UpdatePageStatusAsync(string documentId, string pageId, SyncStatus status, string? error = null)
    {
        var sql = @"
            UPDATE Pages 
            SET Status = @Status, LastError = @Error, LastSyncTime = @SyncTime
            WHERE DocumentId = @DocumentId AND PageId = @PageId";
        
        await _connection!.ExecuteAsync(sql, new
        {
            Status = (int)status,
            Error = error,
            SyncTime = status == SyncStatus.Uploaded ? DateTime.UtcNow.ToString("O") : null,
            DocumentId = documentId,
            PageId = pageId
        });
    }
    
    public async Task<DocumentMetadata?> GetDocumentMetadataAsync(string documentId)
    {
        var sql = "SELECT * FROM Documents WHERE DocumentId = @DocumentId";
        var result = await _connection!.QueryFirstOrDefaultAsync<dynamic>(sql, new { documentId });
        
        if (result == null)
            return null;
        
        var doc = new DocumentMetadata
        {
            DocumentId = result.DocumentId,
            VisibleName = result.VisibleName,
            Type = result.Type,
            Parent = result.Parent ?? "",
            LastModified = DateTime.Parse(result.LastModified)
        };
        
        // Load pages
        var pages = await GetDocumentPagesAsync(documentId);
        doc.Pages.AddRange(pages);
        
        return doc;
    }
    
    private async Task<List<PageMetadata>> GetDocumentPagesAsync(string documentId)
    {
        var sql = "SELECT * FROM Pages WHERE DocumentId = @DocumentId";
        var results = await _connection!.QueryAsync<dynamic>(sql, new { documentId });
        return results.Select(MapToPageMetadata).ToList();
    }
    
    public async Task<List<DocumentMetadata>> GetAllDocumentsAsync()
    {
        var sql = "SELECT * FROM Documents ORDER BY VisibleName";
        var results = await _connection!.QueryAsync<dynamic>(sql);
        
        var documents = new List<DocumentMetadata>();
        foreach (var result in results)
        {
            var doc = new DocumentMetadata
            {
                DocumentId = result.DocumentId,
                VisibleName = result.VisibleName,
                Type = result.Type,
                Parent = result.Parent ?? "",
                LastModified = DateTime.Parse(result.LastModified)
            };
            
            doc.Pages.AddRange(await GetDocumentPagesAsync(doc.DocumentId));
            documents.Add(doc);
        }
        
        return documents;
    }
    
    public async Task SaveDocumentMetadataAsync(DocumentMetadata metadata)
    {
        var sql = @"
            INSERT OR REPLACE INTO Documents (DocumentId, VisibleName, Type, Parent, LastModified, Json)
            VALUES (@DocumentId, @VisibleName, @Type, @Parent, @LastModified, @Json)";
        
        await _connection!.ExecuteAsync(sql, new
        {
            metadata.DocumentId,
            metadata.VisibleName,
            metadata.Type,
            metadata.Parent,
            LastModified = metadata.LastModified.ToString("O"),
            Json = System.Text.Json.JsonSerializer.Serialize(metadata)
        });
        
        // Save pages
        foreach (var page in metadata.Pages)
        {
            await SavePageMetadataAsync(page);
        }
    }
    
    public async Task<long> GetCacheSizeAsync()
    {
        if (string.IsNullOrEmpty(_databasePath))
            return 0;
        
        var cacheDir = Path.Combine(Path.GetDirectoryName(_databasePath)!, "cache");
        if (!Directory.Exists(cacheDir))
            return 0;
        
        return await Task.Run(() =>
        {
            var dir = new DirectoryInfo(cacheDir);
            return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        });
    }
    
    public async Task<int> CleanupOldCacheAsync(int daysToKeep)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep);
        
        var sql = @"
            DELETE FROM Pages 
            WHERE Status = @Status AND LastSyncTime < @CutoffDate";
        
        var deleted = await _connection!.ExecuteAsync(sql, new
        {
            Status = (int)SyncStatus.Uploaded,
            CutoffDate = cutoffDate.ToString("O")
        });
        
        // Also clean up old sync history
        sql = "DELETE FROM SyncHistory WHERE Timestamp < @CutoffDate";
        await _connection.ExecuteAsync(sql, new { CutoffDate = cutoffDate.ToString("O") });
        
        return deleted;
    }
    
    public async Task ClearCacheAsync()
    {
        await _connection!.ExecuteAsync("DELETE FROM Pages");
        await _connection.ExecuteAsync("DELETE FROM Documents");
        await _connection.ExecuteAsync("DELETE FROM SyncHistory");
        
        // Clear cache directory
        if (!string.IsNullOrEmpty(_databasePath))
        {
            var cacheDir = Path.Combine(Path.GetDirectoryName(_databasePath)!, "cache");
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
                Directory.CreateDirectory(cacheDir);
            }
        }
    }
    
    public async Task RecordSyncEventAsync(string documentId, string pageId, bool success, string? details = null)
    {
        var sql = @"
            INSERT INTO SyncHistory (Timestamp, DocumentId, PageId, Success, Details)
            VALUES (@Timestamp, @DocumentId, @PageId, @Success, @Details)";
        
        await _connection!.ExecuteAsync(sql, new
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            DocumentId = documentId,
            PageId = pageId,
            Success = success ? 1 : 0,
            Details = details
        });
    }
    
    public async Task<List<SyncEvent>> GetSyncHistoryAsync(int limit = 100)
    {
        var sql = @"
            SELECT * FROM SyncHistory 
            ORDER BY Timestamp DESC 
            LIMIT @Limit";
        
        var results = await _connection!.QueryAsync<SyncEvent>(sql, new { Limit = limit });
        return results.ToList();
    }
    
    private PageMetadata MapToPageMetadata(dynamic row)
    {
        return new PageMetadata
        {
            DocumentId = row.DocumentId,
            PageId = row.PageId,
            PageNumber = row.PageNumber ?? "",
            Title = row.Title ?? "",
            VirtualPath = row.VirtualPath ?? "",
            LocalFilePath = row.LocalFilePath ?? "",
            CachedFilePath = row.CachedFilePath ?? "",
            FileSizeBytes = row.FileSizeBytes ?? 0,
            LastModified = DateTime.Parse(row.LastModified),
            ContentHash = row.ContentHash ?? "",
            Status = (SyncStatus)(row.Status ?? 0),
            LastSyncTime = row.LastSyncTime != null ? DateTime.Parse(row.LastSyncTime) : null,
            OneNotePageId = row.OneNotePageId,
            OneNotePageUrl = row.OneNotePageUrl,
            RetryCount = row.RetryCount ?? 0,
            LastError = row.LastError
        };
    }
    
    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}