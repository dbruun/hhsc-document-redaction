using Azure.Core;
using Azure.Identity;
using DocumentRedaction.API.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------- Services ----------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// HttpClient factory used by the Azure Language and Document Intelligence REST clients
builder.Services.AddHttpClient(nameof(TextPiiClient));
builder.Services.AddHttpClient(nameof(DocumentLayoutService));

// ---------- Authentication ----------
// The app authenticates to BOTH Azure Blob Storage and Azure AI Language using a
// single Entra ID (Azure AD) identity — no account keys or subscription keys.
// The backing Azure AI Language resource has local (key) auth disabled, so Entra ID
// is the only supported path.
//
// DefaultAzureCredential resolves Managed Identity in Azure, or the signed-in developer
// identity (VS / az login) locally. Pinning TenantId ensures every credential source
// requests tokens for the resources' tenant, avoiding "Issuer validation failed" errors
// on multi-tenant machines.
var tenantId = builder.Configuration["Azure:TenantId"];
var credentialOptions = new DefaultAzureCredentialOptions();
if (!string.IsNullOrWhiteSpace(tenantId))
{
    credentialOptions.TenantId = tenantId;
}
TokenCredential credential = new DefaultAzureCredential(credentialOptions);
builder.Services.AddSingleton(credential);

// Register Azure AI Foundry services
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
builder.Services.AddSingleton<ITextPiiClient, TextPiiClient>();
builder.Services.AddSingleton<IDocumentLayoutService, DocumentLayoutService>();

// Per-format document processors (resolved as IEnumerable by the orchestrator)
builder.Services.AddSingleton<IDocumentFormatProcessor, TxtDocumentProcessor>();
builder.Services.AddSingleton<IDocumentFormatProcessor, DocxDocumentProcessor>();
builder.Services.AddSingleton<IDocumentFormatProcessor, PdfDocumentProcessor>();

builder.Services.AddScoped<IDocumentRedactionOrchestrator, DocumentRedactionOrchestrator>();

// CORS – allow the React dev server and any configured production origin
var allowedOrigins = builder.Configuration
    .GetSection("AllowedOrigins")
    .Get<string[]>() ?? ["http://localhost:5173"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Increase the multipart body limit to 50 MB
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 52_428_800; // 50 MB
});

var app = builder.Build();

// ---------- Middleware ----------
app.UseCors();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Expose for integration tests
public partial class Program { }
