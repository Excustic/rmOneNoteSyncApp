using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services.Platform;

/// <summary>
/// Base implementation with common device detection logic
/// </summary>
public abstract class DeviceDetectionServiceBase : IDeviceDetectionService
{
    protected readonly ILogger _logger;
    private Timer? _pollingTimer;
    private DeviceInfo? _currentDevice;
    private bool _isMonitoring;
    
    protected const string REMARKABLE_USB_IP = "10.11.99.1";
    
    public event EventHandler<DeviceConnectionEventArgs>? DeviceConnectionChanged;
    
    public bool IsConnected => _currentDevice != null;
    public DeviceInfo? CurrentDevice => _currentDevice;
    
    protected DeviceDetectionServiceBase(ILogger logger)
    {
        _logger = logger;
    }
    
    public virtual async Task StartMonitoringAsync()
    {
        if (_isMonitoring) return;
        
        _isMonitoring = true;
        
        // Start polling timer - check every 2 seconds
        _pollingTimer = new Timer(
            async _ => await CheckConnectionAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2));
        
        _logger.LogInformation("Started device monitoring");
    }
    
    public virtual async Task StopMonitoringAsync()
    {
        _isMonitoring = false;
        
        _pollingTimer?.Dispose();
        _pollingTimer = null;
        
        _logger.LogInformation("Stopped device monitoring");
    }
    
    public async Task<bool> CheckConnectionAsync()
    {
        try
        {
            var networkInterface = await FindRemarkableInterfaceAsync();
            
            if (networkInterface == null)
            {
                if (_currentDevice == null) return false;
                // Device was connected but now isn't
                _currentDevice = null;
                DeviceConnectionChanged?.Invoke(this, new DeviceConnectionEventArgs
                {
                    IsConnected = false,
                    Device = null
                });
                return false;
            }
            
            // Verify we can ping the device
            bool canPing = await PingDeviceAsync(REMARKABLE_USB_IP);
            
            if (canPing)
            {
                if (_currentDevice == null)
                {
                    // New device connection
                    _currentDevice = new DeviceInfo
                    {
                        IpAddress = REMARKABLE_USB_IP,
                        InterfaceName = networkInterface.Name,
                        MacAddress = GetMacAddress(networkInterface),
                        DetectedAt = DateTime.UtcNow,
                        ConnectionType = DeviceConnectionType.USB
                    };
                    
                    DeviceConnectionChanged?.Invoke(this, new DeviceConnectionEventArgs
                    {
                        IsConnected = true,
                        Device = _currentDevice
                    });
                }
                return true;
            }
            else if (_currentDevice != null)
            {
                // Lost connection
                _currentDevice = null;
                DeviceConnectionChanged?.Invoke(this, new DeviceConnectionEventArgs
                {
                    IsConnected = false,
                    Device = null
                });
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking device connection");
            return false;
        }
    }
    
    protected abstract Task<NetworkInterface?> FindRemarkableInterfaceAsync();
    
    protected bool HasRemarkableIpInRange(NetworkInterface iface)
    {
        try
        {
            var ipProps = iface.GetIPProperties();
            return ipProps.UnicastAddresses.Any(addr =>
                addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                IsInRemarkableSubnet(addr.Address));
        }
        catch
        {
            return false;
        }
    }
    
    private bool IsInRemarkableSubnet(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && 
               bytes[0] == 10 && 
               bytes[1] == 11 && 
               bytes[2] == 99;
    }
    
    private async Task<bool> PingDeviceAsync(string ipAddress)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ipAddress, 1000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }
    
    private string GetMacAddress(NetworkInterface iface)
    {
        try
        {
            var mac = iface.GetPhysicalAddress();
            var bytes = mac.GetAddressBytes();
            return string.Join(":", bytes.Select(b => b.ToString("X2")));
        }
        catch
        {
            return "Unknown";
        }
    }
    
    public virtual void Dispose()
    {
        StopMonitoringAsync().GetAwaiter().GetResult();
    }
}