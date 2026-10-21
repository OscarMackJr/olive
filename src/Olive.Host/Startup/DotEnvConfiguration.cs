using Microsoft.Extensions.Configuration.Memory;

namespace Olive.Host.Startup;

public static class DotEnvConfiguration
{
    // Inserts the .env values at the lowest priority so environment variables, command line and overrides win.
    public static ConfigurationManager AddDotEnvBelowEnvironment(this ConfigurationManager configuration, string path)
    {
        if (File.Exists(path))
        {
            var values = DotEnvFile.Parse(File.ReadAllText(path)).ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
            ((IConfigurationBuilder)configuration).Sources.Insert(0, new MemoryConfigurationSource { InitialData = values });
        }

        return configuration;
    }
}
