# Shader Tools for Visual Studio

GLSL and HLSL editing in Visual Studio, using [shader-sense](https://github.com/antaalt/shader-sense). WGSL support is basic.

Requires **Visual Studio 2022 17.14+ or Visual Studio 2026, x64**. Most editor testing has been done in VS 2022.

## What it does

- Syntax highlighting, completion, hover information and signature help.
- Compiler diagnostics through glslang, DXC and Naga.
- Go to definition, including files referenced by `#include`.
- A scope/member navigation bar and symbol search in Shader Explorer.
- Include inspection and a configuration action for include diagnostics.
- Folding, parameter hints and dimming of inactive code.
- Shader variants and formatting through clang-format.

The VSIX includes the official shader-language-server 1.6.2 Windows x64 release and its DXC libraries. Formatting needs a separate `clang-format.exe`. Building and testing the extension requires Node.js and the .NET SDK.

## Install and try it

1. [Build the extension](docs/DEVELOPMENT.md).
2. Close Visual Studio and open `ShaderTools.VisualStudio/bin/Release/ShaderTools.VisualStudio.vsix`.
3. Start VS and open [examples/BasicShaders](examples/BasicShaders) as a folder, or open your own shader project.

If the older **GLSL language integration** extension is installed, disable it to avoid competing GLSL editors.

Commands are under **Extensions > Shader Tools**. **Shader Explorer** lists variants and symbols. **Show server output** opens the log; **Restart language server** restarts analysis.

Use **Shader Explorer > Includes > Inspect active shader** to see the server’s active include tree, a list of source directives and the project’s configured defines. Double-click an active include to open it. The directive list also shows possible matches found on disk. Conditions and macro expansions can change which files the compiler uses. An include diagnostic can offer **Open shader configuration** through VS Quick Actions when a project configuration already exists.

Interactive requests have a timeout so a stalled compiler can stop showing a pending result. If analysis remains unresponsive, the extension closes that server session and attempts up to two automatic restarts. Use **Show server output** for request timing and errors, or **Restart language server** to retry manually.

Parameter hints update as you enter or delete individual arguments. Other argument hints stay visible, including in unfinished calls. The editor uses local declarations and cached completion signatures until the server responds. Ambiguous overloads wait for the server when their parameter names differ. Editing a local function's parameters also updates hints at its call sites.

Common shortcuts, with the default VS key bindings:

| Action | Shortcut |
| --- | --- |
| Completion | `Ctrl+Space` |
| Go to definition | `F12` or `Ctrl+click` |
| Signature help | `Ctrl+Shift+Space` |
| Format document | `Ctrl+K, Ctrl+D` |
| Comment / uncomment | `Ctrl+K, Ctrl+C` / `Ctrl+K, Ctrl+U` |

Ctrl+click follows VS's Go To Definition mouse setting. Its handler is included, but a live mouse test is still pending; server-side navigation is covered by the tests.

## Project settings

Use **Extensions > Shader Tools > Project settings** to open the `.shadertools.json` that applies to the active shader. Settings are loaded from the open solution or folder root down to the shader's directory. For example, opening `examples` also loads `examples/BasicShaders/.shadertools.json` for shaders inside BasicShaders. Separate subprojects keep their own settings. Standalone files use the nearest parent configuration.

Nested files override parent values. Objects such as `defines` are merged; arrays such as `includes` are replaced. Relative paths are resolved from the configuration file that declares them. `${workspaceFolder}` still refers to the open solution or folder root.

For example, an OpenGL project can use:

```json
{
  "includes": ["shaders/include"],
  "defines": { "USE_SHADOWS": "1" },
  "glsl": {
    "targetClient": "None",
    "spirvVersion": "None"
  }
}
```

Include paths are relative to the configuration file. Set the GLSL version in the shader's `#version` directive. For Vulkan, use a matching target such as `Vulkan1_3` with `SPIRV1_6`; see [BasicShaders](examples/BasicShaders/.shadertools.json) and the [settings schema](schemas/shadertools.schema.json).

Global settings are under **Tools > Options > Shader Tools > General**. Project settings take precedence. Configuration changes are loaded after saving; if a file is invalid, the last working settings are kept and the error is logged.

**Use C++ colors for shaders** uses Visual Assist colors when available, or the current C++ colors otherwise. Turn it off to use TextMate colors. Classification uses declarations in the current document, so some symbols from includes or macros may keep the default color.

For formatting, the extension looks for clang-format in the VS installation and `PATH`. You can also set **clang-format directory** and restart the language server. A project `.clang-format` file is handled by the formatter.

## HLSL Tools compatibility

Some VS installations also have the separate **HLSL Tools** editor. It reads `shadertoolsconfig.json`, while this extension reads `.shadertools.json`. Restarting our server does not reload the other editor's settings.

BasicShaders includes both configuration files. Close and reopen HLSL tabs after changing the HLSL Tools configuration. If you prefer that editor for HLSL, turn off **Enable HLSL** in our options and reopen the files.

## Shader variants

**Edit shader variants** opens the nearest `.shader-variants.json` for the active shader. **Refresh** in Shader Explorer reads that catalog. Shader paths in a project catalog are relative to its directory. A variant sets the entry point, stage, defines and include paths for one shader.

```json
[
  {
    "name": "Fragment",
    "file": "shaders/material.frag",
    "entryPoint": "main",
    "stage": "fragment",
    "defines": { "USE_SHADOWS": "1" },
    "includes": ["shaders/include"]
  }
]
```

Select a variant in **Shader Explorer > Variants** and click **Activate**. **Automatic** clears the selection. **Refresh** reloads the list; **Load...** also accepts Shader Validator's variant database format. For an HLSL pixel shader, use the stage name `fragment`.

## Limits

- One solution/folder context is used per VS instance. Symbol search covers files indexed by the server.
- Build settings from `.vcxproj`, CMake or a game engine are not imported automatically.
- Symbol rename, find all references, GPU debugging and SPIR-V/DXIL export are not implemented.
- VS 2026 and some editor scenarios, including theme changes and multiple views of a file, still need more manual testing.

Build instructions: [DEVELOPMENT.md](docs/DEVELOPMENT.md). Bundled component licenses: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
