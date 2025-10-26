using System;
using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

public interface ISyncServerService
{
    Task StartAsync(int port = 8080);
    Task StopAsync();
    bool IsRunning { get; }
    event EventHandler<FileReceivedEventArgs>? FileReceived;
}

public class FileReceivedEventArgs : EventArgs
{
    public string DocumentId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string VirtualPath { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime ReceivedAt { get; set; }
}