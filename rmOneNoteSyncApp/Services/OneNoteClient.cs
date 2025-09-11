using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public interface IOneNoteClient
{
    Task<bool> IsAuthenticatedAsync();
    Task<List<Notebook>> GetNotebooksAsync();
    Task<Notebook> CreateNotebookAsync(string displayName);
    Task<List<Section>> GetSectionsAsync(string notebookId);
    Task<Section> CreateSectionAsync(string notebookId, string displayName);
    Task<OneNotePage> CreatePageAsync(string sectionId, string title, string htmlContent);
    Task<OneNotePage> UpdatePageAsync(string pageId, string htmlContent);
    Task<string> UploadInkMLPageAsync(string sectionId, string title, byte[] inkmlData, Dictionary<string, string> metadata);
    Task<bool> DeletePageAsync(string pageId);
    Task<OneNotePage> GetPageAsync(string pageId);
    Task<Stream> GetPageContentAsync(string pageId);
}

public class OneNoteClient : IOneNoteClient
{
    private readonly ILogger<OneNoteClient> _logger;
    private readonly IOneNoteAuthService _authService;
    private GraphServiceClient? _graphClient;
    private readonly HttpClient _httpClient;
    
    // Graph API endpoints
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string OneNoteBaseUrl = "https://graph.microsoft.com/v1.0/me/onenote";
    
    public OneNoteClient(
        ILogger<OneNoteClient> logger,
        IOneNoteAuthService authService)
    {
        _logger = logger;
        _authService = authService;
        _httpClient = new HttpClient();
    }
    
    public async Task<bool> IsAuthenticatedAsync()
    {
        var token = await _authService.GetAccessTokenAsync();
        return !string.IsNullOrEmpty(token);
    }
    
    private async Task<GraphServiceClient> GetGraphClientAsync()
    {
        if (_graphClient == null)
        {
            var token = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("Not authenticated with OneNote");
            }
            
            _graphClient = new GraphServiceClient(new HttpClient(), 
                new DelegateAuthenticationProvider(async (request) =>
                {
                    request.Headers.Authorization = 
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }), GraphBaseUrl);
        }
        
        return _graphClient;
    }
    
    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method, string url)
    {
        var token = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Not authenticated");
        }
        
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = 
            new AuthenticationHeaderValue("Bearer", token);
        
        return request;
    }
    
    public async Task<List<Notebook>> GetNotebooksAsync()
    {
        try
        {
            _logger.LogInformation("Fetching OneNote notebooks");
            
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get, 
                $"{OneNoteBaseUrl}/notebooks");
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ODataResponse<Notebook>>(json);
            
            _logger.LogInformation("Found {Count} notebooks", result?.Value?.Count ?? 0);
            return result?.Value ?? new List<Notebook>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get notebooks");
            throw;
        }
    }
    
    public async Task<Notebook> CreateNotebookAsync(string displayName)
    {
        try
        {
            _logger.LogInformation("Creating notebook: {Name}", displayName);
            
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post, 
                $"{OneNoteBaseUrl}/notebooks");
            
            var body = new { displayName };
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var notebook = JsonSerializer.Deserialize<Notebook>(json);
            
            _logger.LogInformation("Created notebook with ID: {Id}", notebook?.Id);
            return notebook!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create notebook");
            throw;
        }
    }
    
    public async Task<List<Section>> GetSectionsAsync(string notebookId)
    {
        try
        {
            _logger.LogInformation("Fetching sections for notebook: {Id}", notebookId);
            
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get, 
                $"{OneNoteBaseUrl}/notebooks/{notebookId}/sections");
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ODataResponse<Section>>(json);
            
            return result?.Value ?? new List<Section>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sections");
            throw;
        }
    }
    
    public async Task<Section> CreateSectionAsync(string notebookId, string displayName)
    {
        try
        {
            _logger.LogInformation("Creating section '{Name}' in notebook {Id}", 
                displayName, notebookId);
            
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post, 
                $"{OneNoteBaseUrl}/notebooks/{notebookId}/sections");
            
            var body = new { displayName };
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var section = JsonSerializer.Deserialize<Section>(json);
            
            _logger.LogInformation("Created section with ID: {Id}", section?.Id);
            return section!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create section");
            throw;
        }
    }
    
    public async Task<OneNotePage> CreatePageAsync(
        string sectionId, string title, string htmlContent)
    {
        try
        {
            _logger.LogInformation("Creating page '{Title}' in section {Id}", 
                title, sectionId);
            
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post, 
                $"{OneNoteBaseUrl}/sections/{sectionId}/pages");
            
            // OneNote expects HTML content with specific structure
            var fullHtml = WrapInOneNoteHtml(title, htmlContent);
            
            request.Content = new StringContent(fullHtml, Encoding.UTF8, "text/html");
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var page = JsonSerializer.Deserialize<OneNotePage>(json);
            
            _logger.LogInformation("Created page with ID: {Id}", page?.Id);
            return page!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create page");
            throw;
        }
    }
    
    public async Task<string> UploadInkMLPageAsync(
        string sectionId, 
        string title, 
        byte[] inkmlData,
        Dictionary<string, string> metadata)
    {
        try
        {
            _logger.LogInformation("Uploading InkML page '{Title}' to section {Id}", 
                title, sectionId);
            
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                $"{OneNoteBaseUrl}/sections/{sectionId}/pages");
            
            // Create multipart content for InkML upload
            using var content = new MultipartFormDataContent("----Boundary");
            
            // Add the presentation part (HTML that references the InkML)
            var presentationHtml = CreateInkMLPresentationHtml(title, metadata);
            content.Add(new StringContent(presentationHtml, Encoding.UTF8, "text/html"), 
                "Presentation");
            
            // Add the InkML data part
            var inkmlContent = new ByteArrayContent(inkmlData);
            inkmlContent.Headers.ContentType = new MediaTypeHeaderValue("application/inkml+xml");
            content.Add(inkmlContent, "InkML", "drawing.xml");
            
            request.Content = content;
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            // Extract page ID from response headers or body
            var location = response.Headers.Location?.ToString() ?? "";
            var pageId = ExtractPageIdFromLocation(location);
            
            _logger.LogInformation("Uploaded InkML page with ID: {Id}", pageId);
            return pageId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload InkML page");
            throw;
        }
    }
    
    public async Task<OneNotePage> UpdatePageAsync(string pageId, string htmlContent)
    {
        try
        {
            _logger.LogInformation("Updating page {Id}", pageId);
            
            // OneNote uses PATCH with specific JSON format for updates
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Patch,
                $"{OneNoteBaseUrl}/pages/{pageId}/content");
            
            var patchContent = new[]
            {
                new
                {
                    target = "body",
                    action = "replace",
                    content = htmlContent
                }
            };
            
            request.Content = new StringContent(
                JsonSerializer.Serialize(patchContent),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            _logger.LogInformation("Updated page {Id}", pageId);
            
            // Fetch and return updated page
            return await GetPageAsync(pageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update page");
            throw;
        }
    }
    
    public async Task<bool> DeletePageAsync(string pageId)
    {
        try
        {
            _logger.LogInformation("Deleting page {Id}", pageId);
            
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Delete,
                $"{OneNoteBaseUrl}/pages/{pageId}");
            
            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Deleted page {Id}", pageId);
                return true;
            }
            
            _logger.LogWarning("Failed to delete page {Id}: {Status}", 
                pageId, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete page");
            return false;
        }
    }
    
    public async Task<OneNotePage> GetPageAsync(string pageId)
    {
        try
        {
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get,
                $"{OneNoteBaseUrl}/pages/{pageId}");
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<OneNotePage>(json)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get page {Id}", pageId);
            throw;
        }
    }
    
    public async Task<Stream> GetPageContentAsync(string pageId)
    {
        try
        {
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get,
                $"{OneNoteBaseUrl}/pages/{pageId}/content");
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            return await response.Content.ReadAsStreamAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get page content for {Id}", pageId);
            throw;
        }
    }
    
    // Helper method to wrap content in OneNote HTML structure
    private string WrapInOneNoteHtml(string title, string bodyContent)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>{System.Web.HttpUtility.HtmlEncode(title)}</title>
    <meta name='created' content='{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}' />
</head>
<body data-absolute-enabled='true' style='font-family:Calibri;font-size:11pt'>
    {bodyContent}
</body>
</html>";
    }
    
    // Helper method to create HTML presentation for InkML
    private string CreateInkMLPresentationHtml(string title, Dictionary<string, string> metadata)
    {
        var metadataHtml = string.Join("\n", 
            metadata.Select(kvp => 
                $"<p><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key)}:</b> " +
                $"{System.Web.HttpUtility.HtmlEncode(kvp.Value)}</p>"));
        
        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>{System.Web.HttpUtility.HtmlEncode(title)}</title>
    <meta name='created' content='{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}' />
</head>
<body data-absolute-enabled='true'>
    <h1>{System.Web.HttpUtility.HtmlEncode(title)}</h1>
    {metadataHtml}
    <object data-attachment='drawing.xml' type='application/inkml+xml' />
</body>
</html>";
    }
    
    private string ExtractPageIdFromLocation(string location)
    {
        // Extract page ID from location header
        // Format: https://graph.microsoft.com/v1.0/users/{user}/onenote/pages/{pageId}
        var parts = location.Split('/');
        return parts.Length > 0 ? parts[^1] : "";
    }
}

// Supporting models for Graph API responses
public class ODataResponse<T>
{
    public List<T>? Value { get; set; }
}

public class Notebook
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public string? SelfLink { get; set; }
}

public class Section
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public string? ParentNotebook { get; set; }
}

public class OneNotePage
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public string? ContentUrl { get; set; }
    public string? WebUrl { get; set; }
}

// Custom authentication provider for Graph SDK
public class DelegateAuthenticationProvider : IAuthenticationProvider
{
    private readonly Func<HttpRequestMessage, Task> _authenticationDelegate;
    
    public DelegateAuthenticationProvider(Func<HttpRequestMessage, Task> authenticationDelegate)
    {
        _authenticationDelegate = authenticationDelegate;
    }
    
    public async Task AuthenticateRequestAsync(HttpRequestMessage request)
    {
        await _authenticationDelegate(request);
    }

    public Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }
}