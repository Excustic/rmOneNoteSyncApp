using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace rmOneNoteSyncApp.Services.Platform;

/// <summary>
/// Linux-specific implementation using udev or sysfs for device detection
/// </summary>
public class LinuxDeviceDetectionService : DeviceDetectionServiceBase
{
    private FileSystemWatcher? _udevWatcher;
    
    public LinuxDeviceDetectionService(ILogger<LinuxDeviceDetectionService> logger) 
        : base(logger)
    {
    }
    
    protected override async Task<NetworkInterface?> FindRemarkableInterfaceAsync()
    {
        return await Task.Run(() =>
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            
            // On Linux, it's typically usb0 or similar
            var usbInterface = interfaces.FirstOrDefault(i =>
                i.OperationalStatus == OperationalStatus.Up &&
                (i.Name.StartsWith("usb") || i.Name.StartsWith("enp")));
            
            if (usbInterface != null && HasRemarkableIpInRange(usbInterface))
                return usbInterface;
            
            // Fallback to IP range check
            return interfaces.FirstOrDefault(i =>
                i.OperationalStatus == OperationalStatus.Up &&
                HasRemarkableIpInRange(i));
        });
    }
    
    public override async Task StartMonitoringAsync()
    {
        await base.StartMonitoringAsync();
        
        // Monitor /sys/class/net for network interface changes
        try
        {
            var sysNetPath = "/sys/class/net";
            if (Directory.Exists(sysNetPath))
            {
                _udevWatcher = new FileSystemWatcher(sysNetPath);
                _udevWatcher.Created += async (sender, e) =>
                {
                    if (e.Name?.StartsWith("usb") == true || e.Name?.StartsWith("enp") == true)
                    {
                        await Task.Delay(1000); // Give interface time to come up
                        await CheckConnectionAsync();
                    }
                };
                _udevWatcher.Deleted += async (sender, e) =>
                {
                    if (e.Name?.StartsWith("usb") == true || e.Name?.StartsWith("enp") == true)
                    {
                        await CheckConnectionAsync();
                    }
                };
                
                _udevWatcher.EnableRaisingEvents = true;
                _logger.LogInformation("Started Linux network interface monitoring");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start sysfs monitoring, using polling only");
        }
    }
    
    public override async Task StopMonitoringAsync()
    {
        if (_udevWatcher != null)
        {
            _udevWatcher.EnableRaisingEvents = false;
            _udevWatcher.Dispose();
            _udevWatcher = null;
        }
        
        await base.StopMonitoringAsync();
    }
    
    public override void Dispose()
    {
        _udevWatcher?.Dispose();
        base.Dispose();
    }
}