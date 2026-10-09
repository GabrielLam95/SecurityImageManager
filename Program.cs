using FtpMirrorService;
using Microsoft.Extensions.Configuration;
using System.Reflection;

var builder = Host.CreateApplicationBuilder(args);

using var embeddedSettings = Assembly.GetExecutingAssembly()
    .GetManifestResourceStream("FtpMirrorService.appsettings.json")
    ?? throw new InvalidOperationException("Embedded application settings were not found.");
builder.Configuration.AddJsonStream(embeddedSettings);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddWindowsService(options => options.ServiceName = "FTP Mirror Service");
builder.Services
    .AddOptions<FtpMirrorOptions>()
    .Bind(builder.Configuration.GetSection(FtpMirrorOptions.SectionName))
    .Validate(options => FtpMirrorOptionsValidator.TryValidate(options, out _), "FTP mirror configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddSingleton<FtpMirror>();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
