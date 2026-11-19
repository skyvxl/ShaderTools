using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Configuration;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.Options
{
    public enum GlslTarget { None, OpenGL450, Vulkan1_0, Vulkan1_1, Vulkan1_2, Vulkan1_3 }
    public enum SpirvTarget { None, SPIRV1_0, SPIRV1_1, SPIRV1_2, SPIRV1_3, SPIRV1_4, SPIRV1_5, SPIRV1_6 }
    public enum HlslModel { ShaderModel6, ShaderModel6_1, ShaderModel6_2, ShaderModel6_3, ShaderModel6_4, ShaderModel6_5, ShaderModel6_6, ShaderModel6_7, ShaderModel6_8 }

    // Keep the settings page identity stable across namespace and assembly version changes.
    [Guid("f4fe06d8-6156-309d-b308-2ebf880d2276")]
    public sealed class ShaderOptions : DialogPage
    {
        private static ShaderOptions current;
        public static ShaderOptions Current => current ??= LoadEarly();
        public static event EventHandler Changed;
        public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaderTools");
        public static string SettingsPath => Path.Combine(SettingsDirectory, "server-settings.json");
        private static string PreferencesPath => Path.Combine(SettingsDirectory, "preferences.json");

        [Category("Languages"), DisplayName("Enable HLSL"), Description("Use Shader Tools for HLSL files. Turn off to use another HLSL editor; reopen files after changing this."), DefaultValue(true)]
        public bool EnableHlsl { get; set; } = true;
        [Category("Languages"), DisplayName("Enable WGSL"), Description("Basic WGSL highlighting and Naga diagnostics. Reopen files after changing this."), DefaultValue(true)]
        public bool EnableWgsl { get; set; } = true;
        [Category("Editor"), DisplayName("Parameter name hints"), DefaultValue(true)]
        public bool EnableInlayHints { get; set; } = true;
        [Category("Editor"), DisplayName("Format on save"), Description("Format GLSL and HLSL before saving, using clang-format and the document's .clang-format configuration."), DefaultValue(true)]
        public bool FormatOnSave { get; set; } = true;
        [Category("Editor"), DisplayName("Fade inactive preprocessor branches"), DefaultValue(true)]
        public bool FadeInactiveCode { get; set; } = true;
        [Category("Editor"), DisplayName("Use C++ colors for shaders"), Description("Use Visual Assist colors when available, otherwise the current theme's C++ colors, for GLSL/HLSL declarations and references. Turn off to use TextMate colors."), DefaultValue(true)]
        public bool UseCppColors { get; set; } = true;
        [Category("Analysis"), DisplayName("Compiler diagnostics"), DefaultValue(true)]
        public bool Validate { get; set; } = true;
        [Category("Analysis"), DisplayName("Discover include context automatically"), DefaultValue(true)]
        public bool AutomaticVariantDiscovery { get; set; } = true;
        [Category("GLSL"), DisplayName("Target client"), Description("None validates ordinary GLSL without SPIR-V restrictions. Select Vulkan or OpenGL450 with a SPIR-V version for compiled output. Project .shadertools.json can override this.")]
        public GlslTarget TargetClient { get; set; } = GlslTarget.None;
        [Category("GLSL"), DisplayName("SPIR-V version")]
        public SpirvTarget SpirvVersion { get; set; } = SpirvTarget.None;
        [Category("HLSL"), DisplayName("Shader model")]
        public HlslModel ShaderModel { get; set; } = HlslModel.ShaderModel6_8;
        [Category("HLSL"), DisplayName("Enable 16-bit types"), DefaultValue(false)]
        public bool Enable16BitTypes { get; set; }
        [Category("Tools"), DisplayName("Custom language server"), Description("Optional absolute path to shader-language-server.exe compatible with 1.6.2. Empty uses the bundled server. Restart the server after changing this.")]
        public string ServerPath { get; set; } = "";
        [Category("Tools"), DisplayName("clang-format directory"), Description("Optional directory containing clang-format.exe. Empty searches PATH and this Visual Studio's LLVM tools. Restart the server after changing this.")]
        public string FormatDirectory { get; set; } = "";

        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();
            current = this;
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            if ((!string.IsNullOrWhiteSpace(ServerPath) && (!Path.IsPathRooted(ServerPath) || !File.Exists(ServerPath))) ||
                (!string.IsNullOrWhiteSpace(FormatDirectory) && !File.Exists(Path.Combine(FormatDirectory, "clang-format.exe"))))
            {
                _=System.Windows.Forms.MessageBox.Show("Check the custom server path and clang-format directory. Leave them empty to use automatic discovery.", "Shader Tools");
                e.ApplyBehavior = ApplyKind.Cancel;
                return;
            }
            base.OnApply(e);
            current = this;
            SaveSnapshot();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task SaveSnapshotAsync()
        {
            return Task.Run(SaveSnapshot);
        }

        private void SaveSnapshot()
        {
            _=Directory.CreateDirectory(SettingsDirectory);
            JObject config = ShaderConfiguration.CreateDefaultSettings();
            config["validate"] = Validate;
            config["automaticVariantDiscovery"] = AutomaticVariantDiscovery;
            config["glsl"]["targetClient"] = TargetClient.ToString();
            config["glsl"]["spirvVersion"] = SpirvVersion.ToString();
            config["hlsl"]["shaderModel"] = ShaderModel.ToString();
            config["hlsl"]["enable16bitTypes"] = Enable16BitTypes;
            WriteAtomic(SettingsPath, config.ToString(Formatting.Indented));
            JObject preferences = new JObject
            {
                ["enableHlsl"] = EnableHlsl,
                ["enableWgsl"] = EnableWgsl,
                ["enableInlayHints"] = EnableInlayHints,
                ["formatOnSave"] = FormatOnSave,
                ["fadeInactiveCode"] = FadeInactiveCode,
                ["useCppColors"] = UseCppColors,
                ["validate"] = Validate,
                ["automaticVariantDiscovery"] = AutomaticVariantDiscovery,
                ["targetClient"] = TargetClient.ToString(),
                ["spirvVersion"] = SpirvVersion.ToString(),
                ["shaderModel"] = ShaderModel.ToString(),
                ["enable16BitTypes"] = Enable16BitTypes,
                ["serverPath"] = ServerPath,
                ["formatDirectory"] = FormatDirectory
            };
            WriteAtomic(PreferencesPath, preferences.ToString(Formatting.Indented));
        }

        private static void WriteAtomic(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text);
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static ShaderOptions LoadEarly()
        {
            ShaderOptions result = new ShaderOptions();
            try
            {
                if (!File.Exists(PreferencesPath))
                {
                    return result;
                }

                JObject prefs = JObject.Parse(File.ReadAllText(PreferencesPath));
                result.EnableHlsl = (bool?)prefs["enableHlsl"] ?? true;
                result.EnableWgsl = (bool?)prefs["enableWgsl"] ?? true;
                result.EnableInlayHints = (bool?)prefs["enableInlayHints"] ?? true;
                result.FormatOnSave = (bool?)prefs["formatOnSave"] ?? true;
                result.FadeInactiveCode = (bool?)prefs["fadeInactiveCode"] ?? true;
                result.UseCppColors = (bool?)prefs["useCppColors"] ?? true;
                result.Validate = (bool?)prefs["validate"] ?? true;
                result.AutomaticVariantDiscovery = (bool?)prefs["automaticVariantDiscovery"] ?? true;
                if (Enum.TryParse((string)prefs["targetClient"], out GlslTarget target))
                {
                    result.TargetClient = target;
                }

                if (Enum.TryParse((string)prefs["spirvVersion"], out SpirvTarget spirv))
                {
                    result.SpirvVersion = spirv;
                }

                if (Enum.TryParse((string)prefs["shaderModel"], out HlslModel model))
                {
                    result.ShaderModel = model;
                }

                result.Enable16BitTypes = (bool?)prefs["enable16BitTypes"] ?? false;
                result.ServerPath = (string)prefs["serverPath"] ?? "";
                result.FormatDirectory = (string)prefs["formatDirectory"] ?? "";
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
            { System.Diagnostics.Trace.WriteLine("Shader Tools preferences: " + ex.Message); }
            return result;
        }
    }
}
