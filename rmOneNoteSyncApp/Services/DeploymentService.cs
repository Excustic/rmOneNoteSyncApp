using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class DeploymentService : IDeploymentService
{
    private readonly ILogger<DeploymentService> _logger;
    private const string REMOTE_BASE_PATH = "/home/root/onenote-sync";
    
    public event EventHandler<DeploymentProgressEventArgs>? DeploymentProgress;
    
    public DeploymentService(ILogger<DeploymentService> logger)
    {
        _logger = logger;
    }
    
    public async Task<DeploymentResult> CheckInstallationAsync(ISshService sshService)
    {
        var result = new DeploymentResult();
        
        try
        {
            ReportProgress("Checking existing installation...", 0.1, DeploymentStage.Checking);
            
            // Check if directory exists
            var dirCheck = await sshService.ExecuteCommandAsync($"test -d {REMOTE_BASE_PATH} && echo 'exists'");
            if (!dirCheck.Contains("exists"))
            {
                result.IsInstalled = false;
                return result;
            }
            
            // Check version file
            try
            {
                var versionContent = await sshService.ExecuteCommandAsync($"cat {REMOTE_BASE_PATH}/version.json");
                result.InstalledVersion = ExtractVersionFromJson(versionContent);
                result.IsInstalled = true;
            }
            catch
            {
                result.IsInstalled = true;
                result.InstalledVersion = "Unknown";
            }
            
            // Check component status
            result.ComponentStatus["watcher"] = await CheckServiceAsync(sshService, "onenote-sync-watcher");
            result.ComponentStatus["httpclient"] = await CheckServiceAsync(sshService, "onenote-sync-httpclient");
            result.ComponentStatus["cache"] = await CheckFileExistsAsync(sshService, $"{REMOTE_BASE_PATH}/cache/.sync_cache");
            
            result.Success = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check installation");
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        
        return result;
    }
    
    public async Task<DeploymentResult> DeployAsync(ISshService sshService)
    {
        var result = new DeploymentResult();
        
        try
        {
            ReportProgress("Starting deployment...", 0, DeploymentStage.PreparingFiles);
            
            // Step 1: Prepare filesystem
            await PrepareFilesystemAsync(sshService);
            ReportProgress("Filesystem prepared", 0.2, DeploymentStage.PreparingFiles);
            
            // Step 2: Create directory structure
            await CreateDirectoryStructureAsync(sshService);
            ReportProgress("Directory structure created", 0.3, DeploymentStage.PreparingFiles);
            
            // Step 3: Upload binaries
            await UploadBinariesAsync(sshService);
            ReportProgress("Binaries uploaded", 0.5, DeploymentStage.UploadingBinaries);
            
            // Step 4: Upload configuration files
            await UploadConfigurationAsync(sshService);
            ReportProgress("Configuration uploaded", 0.6, DeploymentStage.ConfiguringServices);
            
            // Step 5: Install systemd services
            await InstallSystemdServicesAsync(sshService);
            ReportProgress("Services installed", 0.8, DeploymentStage.ConfiguringServices);
            
            // Step 6: Start services
            await StartServicesAsync(sshService);
            ReportProgress("Services started", 0.9, DeploymentStage.StartingServices);
            
            // Step 7: Verify installation
            var checkResult = await CheckInstallationAsync(sshService);
            result.Success = checkResult.Success && checkResult.IsInstalled;
            result.IsInstalled = checkResult.IsInstalled;
            result.InstalledVersion = "1.0.0";
            result.ComponentStatus = checkResult.ComponentStatus;
            
            ReportProgress("Deployment complete!", 1.0, DeploymentStage.Complete);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deployment failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            ReportProgress($"Deployment failed: {ex.Message}", 0, DeploymentStage.Complete);
        }
        
        return result;
    }
    
    private async Task PrepareFilesystemAsync(ISshService sshService)
    {
        // Make filesystem writable
        await sshService.ExecuteCommandAsync("mount -o remount,rw /");
        
        // Unmount /etc if it's separately mounted
        try
        {
            await sshService.ExecuteCommandAsync("umount /etc -l");
        }
        catch
        {
            // /etc might not be separately mounted, that's OK
        }
    }
    
    private async Task CreateDirectoryStructureAsync(ISshService sshService)
    {
        var directories = new[]
        {
            REMOTE_BASE_PATH,
            $"{REMOTE_BASE_PATH}/bin",
            $"{REMOTE_BASE_PATH}/cache",
            $"{REMOTE_BASE_PATH}/logs",
            $"{REMOTE_BASE_PATH}/debug"
        };
        
        foreach (var dir in directories)
        {
            await sshService.ExecuteCommandAsync($"mkdir -p {dir}");
        }
    }
    
    private async Task UploadBinariesAsync(ISshService sshService)
    {
        // Get embedded resources or local files
        var binaries = new Dictionary<string, string>
        {
            ["watcher"] = "Resources/Binaries/watcher",
            ["httpclient"] = "Resources/Binaries/httpclient",
            ["cache_debug"] = "Resources/Binaries/cache_debug",
            ["watcher_profiler.sh"] = "Resources/Scripts/watcher_profiler.sh"
        };
        
        foreach (var (name, localPath) in binaries)
        {
            var remotePath = name.EndsWith(".sh") 
                ? $"{REMOTE_BASE_PATH}/debug/{name}"
                : $"{REMOTE_BASE_PATH}/bin/{name}";
            
            // For now, we'll use placeholder files
            // In production, these would be embedded resources or downloaded
            if (File.Exists(localPath))
            {
                await sshService.UploadFileAsync(localPath, remotePath);
                await sshService.ExecuteCommandAsync($"chmod +x {remotePath}");
            }
            else
            {
                _logger.LogWarning("Binary not found: {Path}", localPath);
            }
        }
    }
    
    private async Task UploadConfigurationAsync(ISshService sshService)
    {
        // Create configuration files
        var watcherConfig = @"WATCH_PATH=/home/root/.local/share/remarkable/xochitl
            LOG_PATH=/home/root/onenote-sync/logs/watcher.log
            CACHE_PATH=/home/root/onenote-sync/cache/.sync_cache";
        
        var httpclientConfig = @"SERVER_URL=http://localhost:8080/upload
            API_KEY=test-api-key
            SHARED_PATH=*
            UPLOAD_INTERVAL=30
            MAX_RETRIES=5
            RETRY_DELAY=20
            TIMEOUT=10";
        
        var versionJson = @"{
          ""version"": ""1.0.0"",
          ""installed_date"": """ + DateTime.UtcNow.ToString("o") + @""",
          ""components"": {
            ""watcher"": ""1.0.0"",
            ""httpclient"": ""1.0.0"",
            ""cache_format"": ""2""
          }
        }";
        
        // Write configs via SSH
        await sshService.ExecuteCommandAsync($"echo '{watcherConfig}' > {REMOTE_BASE_PATH}/watcher.conf");
        await sshService.ExecuteCommandAsync($"echo '{httpclientConfig}' > {REMOTE_BASE_PATH}/httpclient.conf");
        await sshService.ExecuteCommandAsync($"echo '{versionJson}' > {REMOTE_BASE_PATH}/version.json");
    }
    
    private async Task InstallSystemdServicesAsync(ISshService sshService)
    {
        var watcherService = @"[Unit]
            Description=reMarkable Sync Watcher
            After=home.mount

            [Service]
            Type=simple
            ExecStart=/home/root/onenote-sync/bin/watcher
            Restart=on-failure
            RestartSec=10
            User=root

            [Install]
            WantedBy=multi-user.target";
        
        var httpclientService = @"[Unit]
            Description=reMarkable Sync HTTP Client
            After=home.mount network.target
            Wants=onenote-sync-watcher.service

            [Service]
            Type=simple
            ExecStart=/home/root/onenote-sync/bin/httpclient
            Restart=on-failure
            RestartSec=30
            User=root

            [Install]
            WantedBy=multi-user.target";
        
        // Install service files
        await sshService.ExecuteCommandAsync($"echo '{watcherService}' > /etc/systemd/system/onenote-sync-watcher.service");
        await sshService.ExecuteCommandAsync($"echo '{httpclientService}' > /etc/systemd/system/onenote-sync-httpclient.service");
        
        // Reload systemd
        await sshService.ExecuteCommandAsync("systemctl daemon-reload");
        
        // Enable services
        await sshService.ExecuteCommandAsync("systemctl enable onenote-sync-watcher");
        await sshService.ExecuteCommandAsync("systemctl enable onenote-sync-httpclient");
    }
    
    private async Task StartServicesAsync(ISshService sshService)
    {
        await sshService.ExecuteCommandAsync("systemctl start onenote-sync-watcher");
        await sshService.ExecuteCommandAsync("systemctl start onenote-sync-httpclient");
    }
    
    private async Task<bool> CheckServiceAsync(ISshService sshService, string serviceName)
    {
        try
        {
            var status = await sshService.ExecuteCommandAsync($"systemctl is-active {serviceName}");
            return status.Trim() == "active";
        }
        catch
        {
            return false;
        }
    }
    
    private async Task<bool> CheckFileExistsAsync(ISshService sshService, string path)
    {
        try
        {
            var result = await sshService.ExecuteCommandAsync($"test -f {path} && echo 'exists'");
            return result.Contains("exists");
        }
        catch
        {
            return false;
        }
    }
    
    private string ExtractVersionFromJson(string json)
    {
        // Simple extraction - in production use proper JSON parsing
        var versionStart = json.IndexOf("\"version\":") + 11;
        var versionEnd = json.IndexOf("\"", versionStart);
        return json.Substring(versionStart, versionEnd - versionStart);
    }
    
    private void ReportProgress(string message, double progress, DeploymentStage stage)
    {
        _logger.LogInformation("{Stage}: {Message} ({Progress:P})", stage, message, progress);
        DeploymentProgress?.Invoke(this, new DeploymentProgressEventArgs
        {
            Message = message,
            Progress = progress,
            Stage = stage
        });
    }
    
    public async Task<DeploymentResult> UpdateAsync(ISshService sshService)
    {
        // For updates, we backup config, deploy new version, restore config
        var backupPath = Path.GetTempFileName();
        
        try
        {
            await BackupConfigurationAsync(sshService, backupPath);
            var result = await DeployAsync(sshService);
            await RestoreConfigurationAsync(sshService, backupPath);
            return result;
        }
        finally
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }
    
    public async Task<DeploymentResult> UninstallAsync(ISshService sshService)
    {
        var result = new DeploymentResult();
        
        try
        {
            // Stop services
            await sshService.ExecuteCommandAsync("systemctl stop onenote-sync-watcher");
            await sshService.ExecuteCommandAsync("systemctl stop onenote-sync-httpclient");
            
            // Disable services
            await sshService.ExecuteCommandAsync("systemctl disable onenote-sync-watcher");
            await sshService.ExecuteCommandAsync("systemctl disable onenote-sync-httpclient");
            
            // Remove service files
            await sshService.ExecuteCommandAsync("rm -f /etc/systemd/system/onenote-sync-*.service");
            
            // Remove installation directory
            await sshService.ExecuteCommandAsync($"rm -rf {REMOTE_BASE_PATH}");
            
            result.Success = true;
            result.IsInstalled = false;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        
        return result;
    }
    
    public async Task<bool> BackupConfigurationAsync(ISshService sshService, string localPath)
    {
        try
        {
            await sshService.DownloadFileAsync($"{REMOTE_BASE_PATH}/watcher.conf", localPath + ".watcher");
            await sshService.DownloadFileAsync($"{REMOTE_BASE_PATH}/httpclient.conf", localPath + ".httpclient");
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public async Task<bool> RestoreConfigurationAsync(ISshService sshService, string localPath)
    {
        try
        {
            if (File.Exists(localPath + ".watcher"))
                await sshService.UploadFileAsync(localPath + ".watcher", $"{REMOTE_BASE_PATH}/watcher.conf");
            
            if (File.Exists(localPath + ".httpclient"))
                await sshService.UploadFileAsync(localPath + ".httpclient", $"{REMOTE_BASE_PATH}/httpclient.conf");
            
            return true;
        }
        catch
        {
            return false;
        }
    }
}