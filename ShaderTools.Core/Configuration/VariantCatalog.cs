using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace ShaderTools.Core.Configuration;

public static class VariantCatalog
{
    private static readonly IDictionary<string, string> StageAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["vertex"] = "vertex",
        ["fragment"] = "fragment",
        ["compute"] = "compute",
        ["tesselationControl"] = "tesselationControl",
        ["tesselation_control"] = "tesselationControl",
        ["tessellationControl"] = "tesselationControl",
        ["tessellation_control"] = "tesselationControl",
        ["tesselationEvaluation"] = "tesselationEvaluation",
        ["tesselation_evaluation"] = "tesselationEvaluation",
        ["tessellationEvaluation"] = "tesselationEvaluation",
        ["tessellation_evaluation"] = "tesselationEvaluation",
        ["mesh"] = "mesh",
        ["task"] = "task",
        ["geometry"] = "geometry",
        ["rayGeneration"] = "rayGeneration",
        ["ray_generation"] = "rayGeneration",
        ["closestHit"] = "closestHit",
        ["closest_hit"] = "closestHit",
        ["anyHit"] = "anyHit",
        ["any_hit"] = "anyHit",
        ["callable"] = "callable",
        ["miss"] = "miss",
        ["intersect"] = "intersect",
    };

    private static readonly HashSet<string> NativeProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "file", "entryPoint", "stage", "defines", "includes",
    };

    private static readonly HashSet<string> DatabaseFileProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "uri", "variants",
    };

    private static readonly HashSet<string> DatabaseVariantProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "stage", "defines", "includes",
    };

    private static readonly HashSet<string> GlslExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".glsl", ".vert", ".frag", ".geom", ".comp", ".tesc", ".tese", ".vs", ".fs", ".gs",
        ".vsh", ".fsh", ".gsh", ".mesh", ".task", ".rgen", ".rint", ".rahit", ".rchit",
        ".rmiss", ".rcall",
    };

    private static readonly HashSet<string> HlslExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".hlsl", ".hlsli", ".fx", ".fxh",
    };

    public const string FileName = ".shader-variants.json";

    public static string FindProjectCatalog(string workspaceRoot, string? shaderFile)
    {
        if (string.IsNullOrEmpty(shaderFile)) { return Path.Combine(workspaceRoot, FileName); }
        string catalog = ShaderConfiguration.FindProjectFile(workspaceRoot, shaderFile!, FileName);
        if (File.Exists(catalog)) { return catalog; }
        return Path.Combine(Path.GetDirectoryName(ShaderConfiguration.FindConfigurationPath(workspaceRoot, shaderFile!))!, FileName);
    }

    public static IReadOnlyList<ShaderVariant> Load(string filePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A variant catalog path is required.", nameof(filePath));
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        }

        string absoluteFilePath = Path.GetFullPath(filePath);
        string absoluteWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        if (!File.Exists(absoluteFilePath))
        {
            return Array.Empty<ShaderVariant>();
        }

        JToken token = ConfigurationJson.ReadJson(absoluteFilePath);
        if (token is not JArray entries)
        {
            throw ConfigurationJson.Invalid(absoluteFilePath, "$", "must be a JSON array");
        }

        List<ShaderVariant> variants = [];
        bool databaseFormat = DetectDatabaseFormat(entries, absoluteFilePath);
        if (databaseFormat)
        {
            LoadDatabase(entries, absoluteFilePath, absoluteWorkspaceRoot, variants);
        }
        else
        {
            LoadNative(entries, absoluteFilePath, absoluteWorkspaceRoot, variants);
        }

        RejectDuplicates(variants, absoluteFilePath);
        return new ReadOnlyCollection<ShaderVariant>(variants);
    }

    private static bool DetectDatabaseFormat(JArray entries, string filePath)
    {
        if (entries.Count == 0)
        {
            return false;
        }

        if (entries[0] is not JObject first)
        {
            throw ConfigurationJson.Invalid(filePath, "[0]", "must be an object");
        }

        bool database = first["uri"] != null || first["variants"] != null;
        for (int index = 1; index < entries.Count; index++)
        {
            if (entries[index] is not JObject entry)
            {
                throw ConfigurationJson.Invalid(filePath, $"[{index}]", "must be an object");
            }

            bool entryIsDatabase = entry["uri"] != null || entry["variants"] != null;
            if (entryIsDatabase != database)
            {
                throw ConfigurationJson.Invalid(filePath, $"[{index}]", "cannot mix native variants and shader-validator database entries");
            }
        }

        return database;
    }

    private static void LoadNative(JArray entries, string filePath, string workspaceRoot, ICollection<ShaderVariant> result)
    {
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index] is not JObject entry)
            {
                throw ConfigurationJson.Invalid(filePath, $"[{index}]", "must be an object");
            }

            string path = $"[{index}]";
            ConfigurationJson.RejectUnknownProperties(entry, NativeProperties, filePath, path);
            string name = ConfigurationJson.RequireString(entry, "name", filePath, path);
            string shaderFile = ConfigurationJson.RequireString(entry, "file", filePath, path);
            string entryPoint = ConfigurationJson.RequireString(entry, "entryPoint", filePath, path);
            string absoluteShaderFile = ResolveShaderPath(shaderFile, workspaceRoot, filePath, path + ".file");
            result.Add(CreateVariant(entry, name, absoluteShaderFile, entryPoint, filePath, path, path + ".file", workspaceRoot));
        }
    }

    private static void LoadDatabase(JArray entries, string filePath, string workspaceRoot, ICollection<ShaderVariant> result)
    {
        for (int fileIndex = 0; fileIndex < entries.Count; fileIndex++)
        {
            JObject fileEntry = (JObject)entries[fileIndex];
            string filePathInJson = $"[{fileIndex}]";
            ConfigurationJson.RejectUnknownProperties(fileEntry, DatabaseFileProperties, filePath, filePathInJson);
            string uri = ConfigurationJson.RequireString(fileEntry, "uri", filePath, filePathInJson);
            string absoluteShaderFile = ResolveShaderPath(uri, workspaceRoot, filePath, filePathInJson + ".uri");

            if (fileEntry["variants"] is not JArray variants)
            {
                throw ConfigurationJson.Invalid(filePath, filePathInJson + ".variants", "must be an array");
            }

            for (int variantIndex = 0; variantIndex < variants.Count; variantIndex++)
            {
                string variantPath = $"{filePathInJson}.variants[{variantIndex}]";
                if (variants[variantIndex] is not JObject entry)
                {
                    throw ConfigurationJson.Invalid(filePath, variantPath, "must be an object");
                }

                ConfigurationJson.RejectUnknownProperties(entry, DatabaseVariantProperties, filePath, variantPath);
                string name = ConfigurationJson.RequireString(entry, "name", filePath, variantPath);
                result.Add(CreateVariant(entry, name, absoluteShaderFile, name, filePath, variantPath, filePathInJson + ".uri", workspaceRoot));
            }
        }
    }

    private static ShaderVariant CreateVariant(
        JObject entry,
        string name,
        string shaderFile,
        string entryPoint,
        string catalogPath,
        string propertyPath,
        string shaderPropertyPath,
        string workspaceRoot)
    {
        string? stage = NormalizeStage(entry["stage"], catalogPath, propertyPath + ".stage");
        JObject definesJson = ConfigurationJson.OptionalStringMap(entry, "defines", catalogPath, propertyPath);
        JArray includesJson = ConfigurationJson.OptionalStringArray(entry, "includes", catalogPath, propertyPath);

        Dictionary<string, string> defines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JProperty property in definesJson.Properties())
        {
            defines.Add(
                property.Name,
                ConfigurationJson.ResolveText(property.Value.Value<string>()!, workspaceRoot, catalogPath, propertyPath + ".defines." + property.Name));
        }

        List<string> includes = new List<string>(includesJson.Count);
        for (int index = 0; index < includesJson.Count; index++)
        {
            includes.Add(ConfigurationJson.ResolvePath(
                includesJson[index].Value<string>()!,
                workspaceRoot,
                workspaceRoot,
                catalogPath,
                $"{propertyPath}.includes[{index}]"));
        }

        return new ShaderVariant(
            name,
            shaderFile,
            entryPoint,
            stage,
            new ReadOnlyDictionary<string, string>(defines),
            new ReadOnlyCollection<string>(includes),
            GetShadingLanguage(shaderFile, catalogPath, shaderPropertyPath));
    }

    private static string? NormalizeStage(JToken? token, string filePath, string propertyPath)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (token.Type != JTokenType.String)
        {
            throw ConfigurationJson.Invalid(filePath, propertyPath, "must be a string");
        }

        string value = token.Value<string>()!;
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
        {
            return null;
        }

        if (!StageAliases.TryGetValue(value, out string? normalized))
        {
            throw ConfigurationJson.Invalid(filePath, propertyPath, $"'{value}' is not a supported shader stage");
        }

        return normalized;
    }

    private static string ResolveShaderPath(string value, string workspaceRoot, string catalogPath, string propertyPath)
    {
        string expanded = ConfigurationJson.ResolveText(value, workspaceRoot, catalogPath, propertyPath);
        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        if (Uri.TryCreate(expanded, UriKind.Absolute, out Uri? uri))
        {
            if (!uri.IsFile)
            {
                throw ConfigurationJson.Invalid(catalogPath, propertyPath, "must be a local file path or file URI");
            }

            return Path.GetFullPath(uri.LocalPath);
        }

        return ConfigurationJson.ResolvePath(expanded, workspaceRoot, workspaceRoot, catalogPath, propertyPath);
    }

    private static string GetShadingLanguage(string shaderFile, string catalogPath, string propertyPath)
    {
        string extension = Path.GetExtension(shaderFile);
        if (GlslExtensions.Contains(extension))
        {
            return "Glsl";
        }

        if (HlslExtensions.Contains(extension))
        {
            return "Hlsl";
        }

        if (string.Equals(extension, ".wgsl", StringComparison.OrdinalIgnoreCase))
        {
            return "Wgsl";
        }

        throw ConfigurationJson.Invalid(catalogPath, propertyPath, $"extension '{extension}' is not supported");
    }

    private static void RejectDuplicates(IEnumerable<ShaderVariant> variants, string catalogPath)
    {
        Dictionary<string, HashSet<string>> namesByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (ShaderVariant variant in variants)
        {
            if (!namesByFile.TryGetValue(variant.FilePath, out HashSet<string>? names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                namesByFile.Add(variant.FilePath, names);
            }

            if (!names.Add(variant.Name))
            {
                throw new InvalidDataException(
                    $"Invalid variant catalog '{catalogPath}': duplicate variant name '{variant.Name}' for shader '{variant.FilePath}'.");
            }
        }
    }
}
