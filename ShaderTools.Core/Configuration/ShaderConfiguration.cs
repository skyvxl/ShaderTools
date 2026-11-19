using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace ShaderTools.Core.Configuration;

public sealed class ShaderConfiguration
{
    public const string FileName = ".shadertools.json";

    private static readonly HashSet<string> RootProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "$schema",
        "includes",
        "defines",
        "pathRemapping",
        "validate",
        "symbols",
        "symbolDiagnostics",
        "validateConfig",
        "automaticVariantDiscovery",
        "experimentalMacroExpansion",
        "stageDefine",
        "trace",
        "severity",
        "configOverride",
        "hlsl",
        "glsl",
        "wgsl",
        "serverPath",
        "useWasiServer",
        "updateSymbolsOnVariantUpdate",
    };

    private static readonly HashSet<string> StageNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "vertex",
        "fragment",
        "compute",
        "tesselationControl",
        "tesselationEvaluation",
        "mesh",
        "task",
        "geometry",
        "rayGeneration",
        "closestHit",
        "anyHit",
        "callable",
        "miss",
        "intersect",
    };

    private static readonly HashSet<string> HlslProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "enabled",
        "shaderModel",
        "version",
        "enable16bitTypes",
        "spirv",
    };

    private static readonly HashSet<string> GlslProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "enabled",
        "targetClient",
        "spirvVersion",
        "preamble",
    };

    private static readonly HashSet<string> WgslProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "enabled",
    };

    internal ShaderConfiguration(string workspaceRoot, string configurationPath, JObject settings)
    {
        WorkspaceRoot = workspaceRoot;
        ConfigurationPath = configurationPath;
        Settings = settings;
    }

    public string WorkspaceRoot { get; }

    public string ConfigurationPath { get; }

    public JObject Settings { get; }

    // Only ancestors of this document participate; sibling projects are independent.
    public static ShaderConfiguration LoadForDocument(string workspaceRoot, string filePath, string? userSettingsPath = null)
    {
        string root = GetDocumentRoot(workspaceRoot, filePath);
        ShaderConfiguration configuration = Load(root, userSettingsPath);
        string configurationPath = configuration.ConfigurationPath;
        foreach (string directory in GetDocumentDirectories(root, filePath))
        {
            string path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                Merge(configuration.Settings, LoadLayer(path, directory, root));
                configurationPath = path;
            }
        }

        return new ShaderConfiguration(root, configurationPath, configuration.Settings);
    }

    public static string FindConfigurationPath(string workspaceRoot, string filePath)
    {
        return FindProjectFile(workspaceRoot, filePath, FileName);
    }

    internal static string FindProjectFile(string workspaceRoot, string filePath, string fileName)
    {
        string root = GetDocumentRoot(workspaceRoot, filePath);
        string path = Path.Combine(root, fileName);
        foreach (string directory in GetDocumentDirectories(root, filePath))
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) { path = candidate; }
        }
        return path;
    }

    internal static string GetDocumentRoot(string workspaceRoot, string filePath)
    {
        string documentDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            string root = Path.GetFullPath(workspaceRoot);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.Equals(documentDirectory, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                (documentDirectory + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
        }

        for (DirectoryInfo? directory = new DirectoryInfo(documentDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, FileName))) { return directory.FullName; }
        }
        return documentDirectory;
    }

    private static IEnumerable<string> GetDocumentDirectories(string root, string filePath)
    {
        Stack<string> directories = new Stack<string>();
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (DirectoryInfo? directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            directory != null && !string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), normalizedRoot, StringComparison.OrdinalIgnoreCase);
            directory = directory.Parent)
        {
            directories.Push(directory.FullName);
        }
        return directories;
    }

    public static ShaderConfiguration Load(string workspaceRoot, string? userSettingsPath = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        }

        string absoluteWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        string configurationPath = Path.Combine(absoluteWorkspaceRoot, FileName);
        JObject settings = CreateDefaultSettings();

        if (!string.IsNullOrWhiteSpace(userSettingsPath))
        {
            string absoluteUserPath = Path.GetFullPath(userSettingsPath!);
            if (File.Exists(absoluteUserPath))
            {
                string baseDirectory = Path.GetDirectoryName(absoluteUserPath) ?? absoluteWorkspaceRoot;
                Merge(settings, LoadLayer(absoluteUserPath, baseDirectory, absoluteWorkspaceRoot));
            }
        }

        if (File.Exists(configurationPath))
        {
            Merge(settings, LoadLayer(configurationPath, absoluteWorkspaceRoot, absoluteWorkspaceRoot));
        }

        RemoveClientOnlySettings(settings);
        return new ShaderConfiguration(absoluteWorkspaceRoot, configurationPath, settings);
    }

    public static JObject CreateDefaultSettings()
    {
        JObject stageDefine = [];
        foreach (string stageName in StageNames)
        {
            stageDefine[stageName] = new JObject();
        }

        return new JObject
        {
            ["includes"] = new JArray(),
            ["defines"] = new JObject(),
            ["pathRemapping"] = new JObject(),
            ["validate"] = true,
            ["symbols"] = true,
            ["symbolDiagnostics"] = false,
            ["validateConfig"] = true,
            ["automaticVariantDiscovery"] = false,
            ["experimentalMacroExpansion"] = false,
            ["stageDefine"] = stageDefine,
            ["trace"] = new JObject { ["server"] = "off" },
            ["severity"] = "info",
            ["configOverride"] = string.Empty,
            ["hlsl"] = new JObject
            {
                ["shaderModel"] = "ShaderModel6_8",
                ["version"] = "V2021",
                ["enable16bitTypes"] = false,
                ["spirv"] = false,
            },
            ["glsl"] = new JObject
            {
                ["targetClient"] = "Vulkan1_3",
                ["spirvVersion"] = "SPIRV1_6",
                ["preamble"] = string.Empty,
            },
        };
    }

    private static JObject LoadLayer(string filePath, string baseDirectory, string workspaceRoot)
    {
        JToken token = ConfigurationJson.ReadJson(filePath);
        if (token is not JObject layer)
        {
            throw ConfigurationJson.Invalid(filePath, "$", "must be a JSON object");
        }

        ValidateLayer(layer, filePath);
        ExpandTextValues(layer, workspaceRoot, filePath);
        ResolvePaths(layer, baseDirectory, workspaceRoot, filePath);
        RemoveClientOnlySettings(layer);
        return layer;
    }

    private static void ValidateLayer(JObject layer, string filePath)
    {
        ConfigurationJson.RejectUnknownProperties(layer, RootProperties, filePath, string.Empty);

        ValidateBoolean(layer, "validate", filePath, string.Empty);
        ValidateBoolean(layer, "symbols", filePath, string.Empty);
        ValidateBoolean(layer, "symbolDiagnostics", filePath, string.Empty);
        ValidateBoolean(layer, "validateConfig", filePath, string.Empty);
        ValidateBoolean(layer, "automaticVariantDiscovery", filePath, string.Empty);
        ValidateBoolean(layer, "experimentalMacroExpansion", filePath, string.Empty);
        ValidateBoolean(layer, "useWasiServer", filePath, string.Empty);
        ValidateBoolean(layer, "updateSymbolsOnVariantUpdate", filePath, string.Empty);
        ValidateOptionalString(layer, "$schema", filePath, string.Empty);
        ValidateOptionalString(layer, "configOverride", filePath, string.Empty);
        ValidateOptionalString(layer, "serverPath", filePath, string.Empty);
        ValidateEnum(layer, "severity", filePath, string.Empty, "none", "error", "warning", "info", "hint");

        if (layer["includes"] != null)
        {
            _=ConfigurationJson.OptionalStringArray(layer, "includes", filePath, string.Empty);
        }

        if (layer["defines"] != null)
        {
            _=ConfigurationJson.OptionalStringMap(layer, "defines", filePath, string.Empty);
        }

        if (layer["pathRemapping"] != null)
        {
            _=ConfigurationJson.OptionalStringMap(layer, "pathRemapping", filePath, string.Empty);
        }

        ValidateStageDefines(layer, filePath);
        ValidateTrace(layer, filePath);
        ValidateHlsl(layer, filePath);
        ValidateGlsl(layer, filePath);
        ValidateWgsl(layer, filePath);
    }

    private static void ValidateStageDefines(JObject layer, string filePath)
    {
        JToken? token = layer["stageDefine"];
        if (token == null)
        {
            return;
        }

        if (token is not JObject stageDefine)
        {
            throw ConfigurationJson.Invalid(filePath, "stageDefine", "must be an object");
        }

        ConfigurationJson.RejectUnknownProperties(stageDefine, StageNames, filePath, "stageDefine");
        foreach (JProperty property in stageDefine.Properties())
        {
            if (property.Value is not JObject defines)
            {
                throw ConfigurationJson.Invalid(filePath, "stageDefine." + property.Name, "must be an object whose values are strings");
            }

            ConfigurationJson.ValidateStringMap(defines, filePath, "stageDefine." + property.Name);
        }
    }

    private static void ValidateTrace(JObject layer, string filePath)
    {
        JToken? token = layer["trace"];
        if (token == null)
        {
            return;
        }

        if (token is not JObject trace)
        {
            throw ConfigurationJson.Invalid(filePath, "trace", "must be an object");
        }

        ConfigurationJson.RejectUnknownProperties(
            trace,
            new HashSet<string>(StringComparer.Ordinal) { "server" },
            filePath,
            "trace");
        ValidateEnum(trace, "server", filePath, "trace", "off", "messages", "verbose");
    }

    private static void ValidateHlsl(JObject layer, string filePath)
    {
        JObject? hlsl = OptionalObject(layer, "hlsl", filePath, string.Empty);
        if (hlsl == null)
        {
            return;
        }

        ConfigurationJson.RejectUnknownProperties(hlsl, HlslProperties, filePath, "hlsl");
        ValidateBoolean(hlsl, "enabled", filePath, "hlsl");
        ValidateBoolean(hlsl, "enable16bitTypes", filePath, "hlsl");
        ValidateBoolean(hlsl, "spirv", filePath, "hlsl");
        ValidateEnum(hlsl, "version", filePath, "hlsl", "V2016", "V2017", "V2018", "V2021");
        ValidateEnum(
            hlsl,
            "shaderModel",
            filePath,
            "hlsl",
            "ShaderModel6",
            "ShaderModel6_1",
            "ShaderModel6_2",
            "ShaderModel6_3",
            "ShaderModel6_4",
            "ShaderModel6_5",
            "ShaderModel6_6",
            "ShaderModel6_7",
            "ShaderModel6_8");
    }

    private static void ValidateGlsl(JObject layer, string filePath)
    {
        JObject? glsl = OptionalObject(layer, "glsl", filePath, string.Empty);
        if (glsl == null)
        {
            return;
        }

        ConfigurationJson.RejectUnknownProperties(glsl, GlslProperties, filePath, "glsl");
        ValidateBoolean(glsl, "enabled", filePath, "glsl");
        ValidateOptionalString(glsl, "preamble", filePath, "glsl");
        ValidateEnum(glsl, "targetClient", filePath, "glsl", "None", "Vulkan1_0", "Vulkan1_1", "Vulkan1_2", "Vulkan1_3", "OpenGL450");
        ValidateEnum(glsl, "spirvVersion", filePath, "glsl", "None", "SPIRV1_0", "SPIRV1_1", "SPIRV1_2", "SPIRV1_3", "SPIRV1_4", "SPIRV1_5", "SPIRV1_6");
    }

    private static void ValidateWgsl(JObject layer, string filePath)
    {
        JObject? wgsl = OptionalObject(layer, "wgsl", filePath, string.Empty);
        if (wgsl == null)
        {
            return;
        }

        ConfigurationJson.RejectUnknownProperties(wgsl, WgslProperties, filePath, "wgsl");
        ValidateBoolean(wgsl, "enabled", filePath, "wgsl");
    }

    private static JObject? OptionalObject(JObject parent, string name, string filePath, string path)
    {
        JToken? token = parent[name];
        if (token == null)
        {
            return null;
        }

        if (token is not JObject result)
        {
            throw ConfigurationJson.Invalid(filePath, ConfigurationJson.JoinPath(path, name), "must be an object");
        }

        return result;
    }

    private static void ValidateBoolean(JObject parent, string name, string filePath, string path)
    {
        JToken? token = parent[name];
        if (token != null && token.Type != JTokenType.Boolean)
        {
            throw ConfigurationJson.Invalid(filePath, ConfigurationJson.JoinPath(path, name), "must be a boolean");
        }
    }

    private static void ValidateOptionalString(JObject parent, string name, string filePath, string path)
    {
        JToken? token = parent[name];
        if (token != null && token.Type != JTokenType.String)
        {
            throw ConfigurationJson.Invalid(filePath, ConfigurationJson.JoinPath(path, name), "must be a string");
        }
    }

    private static void ValidateEnum(JObject parent, string name, string filePath, string path, params string[] allowedValues)
    {
        JToken? token = parent[name];
        if (token == null)
        {
            return;
        }

        string propertyPath = ConfigurationJson.JoinPath(path, name);
        if (token.Type != JTokenType.String)
        {
            throw ConfigurationJson.Invalid(filePath, propertyPath, "must be a string");
        }

        string? value = token.Value<string>();
        if (Array.IndexOf(allowedValues, value) < 0)
        {
            throw ConfigurationJson.Invalid(filePath, propertyPath, $"must be one of: {string.Join(", ", allowedValues)}");
        }
    }

    private static void ExpandTextValues(JToken token, string workspaceRoot, string filePath)
    {
        foreach (JToken child in token.Children())
        {
            if (child is JProperty property && property.Value.Type == JTokenType.String)
            {
                property.Value = ConfigurationJson.ResolveText(
                    property.Value.Value<string>()!,
                    workspaceRoot,
                    filePath,
                    property.Path);
            }
            else
            {
                ExpandTextValues(child, workspaceRoot, filePath);
            }
        }
    }

    private static void ResolvePaths(JObject layer, string baseDirectory, string workspaceRoot, string filePath)
    {
        if (layer["includes"] is JArray includes)
        {
            for (int index = 0; index < includes.Count; index++)
            {
                includes[index] = ConfigurationJson.ResolvePath(includes[index].Value<string>()!, baseDirectory, workspaceRoot, filePath, $"includes[{index}]");
            }
        }

        if (layer["pathRemapping"] is JObject remapping)
        {
            foreach (JProperty property in remapping.Properties())
            {
                property.Value = ConfigurationJson.ResolvePath(property.Value.Value<string>()!, baseDirectory, workspaceRoot, filePath, "pathRemapping." + property.Name);
            }
        }

        if (layer["configOverride"] is JValue configOverride)
        {
            configOverride.Value = ConfigurationJson.ResolvePath(configOverride.Value<string>()!, baseDirectory, workspaceRoot, filePath, "configOverride");
        }

        if (layer.SelectToken("glsl.preamble") is JValue preamble)
        {
            preamble.Value = ConfigurationJson.ResolvePath(preamble.Value<string>()!, baseDirectory, workspaceRoot, filePath, "glsl.preamble");
        }
    }

    private static void Merge(JObject target, JObject source)
    {
        foreach (JProperty property in source.Properties())
        {
            if (property.Value is JObject sourceObject && target[property.Name] is JObject targetObject)
            {
                Merge(targetObject, sourceObject);
            }
            else
            {
                target[property.Name] = property.Value.DeepClone();
            }
        }
    }

    private static void RemoveClientOnlySettings(JObject settings)
    {
        _=settings.Remove("$schema");
        _=settings.Remove("serverPath");
        _=settings.Remove("useWasiServer");
        _=settings.Remove("updateSymbolsOnVariantUpdate");

        if (settings["hlsl"] is JObject hlsl)
        {
            _=hlsl.Remove("enabled");
        }

        if (settings["glsl"] is JObject glsl)
        {
            _=glsl.Remove("enabled");
        }

        _=settings.Remove("wgsl");
    }
}
