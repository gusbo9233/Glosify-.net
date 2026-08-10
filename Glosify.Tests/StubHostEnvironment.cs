using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Glosify.Tests;

internal sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Glosify.Tests";
    public string ContentRootPath { get; set; } = "/";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
