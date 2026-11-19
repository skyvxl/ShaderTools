using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace ShaderTools.Core.Configuration;

/// <summary>Keeps independent last known good settings while configuration files are being edited.</summary>
public sealed class ShaderConfigurationStore
{
    private const int MaximumEntries = 256;
    private readonly string? userSettingsPath;
    private readonly Action<string> log;
    private readonly object gate = new object();
    private readonly Dictionary<string, ShaderConfiguration> configurations = new Dictionary<string, ShaderConfiguration>(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> insertionOrder = new Queue<string>();

    public ShaderConfigurationStore(string? userSettingsPath = null, Action<string>? log = null)
    {
        this.userSettingsPath = userSettingsPath;
        this.log = log ?? (_ => { });
    }

    public ShaderConfiguration LoadWorkspace(string workspaceRoot)
    {
        return Load(Path.GetFullPath(workspaceRoot), null);
    }

    public ShaderConfiguration LoadDocument(string workspaceRoot, string filePath)
    {
        return Load(ShaderConfiguration.GetDocumentRoot(workspaceRoot, filePath), Path.GetFullPath(filePath));
    }

    private ShaderConfiguration Load(string root, string? filePath)
    {
        lock (gate)
        {
            string key = root + "\n" + filePath;
            ShaderConfiguration configuration;
            try
            {
                configuration = filePath == null ? ShaderConfiguration.Load(root, userSettingsPath) :
                    ShaderConfiguration.LoadForDocument(root, filePath, userSettingsPath);
                if (!configurations.ContainsKey(key))
                {
                    if (configurations.Count == MaximumEntries) { _=configurations.Remove(insertionOrder.Dequeue()); }
                    insertionOrder.Enqueue(key);
                }
                configurations[key] = configuration;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
            {
                log("Shader settings unchanged for " + (filePath ?? root) + ": " + ex.Message);
                if (!configurations.TryGetValue(key, out configuration))
                {
                    configuration = filePath == null ?
                        new ShaderConfiguration(root, string.Empty, ShaderConfiguration.CreateDefaultSettings()) : Load(root, null);
                }
            }
            return new ShaderConfiguration(configuration.WorkspaceRoot, configuration.ConfigurationPath, (JObject)configuration.Settings.DeepClone());
        }
    }
}
