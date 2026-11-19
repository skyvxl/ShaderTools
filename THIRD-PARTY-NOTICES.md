# Third-party notices

This distribution contains the following third-party components. The license
files named below are shipped in the `licenses` directory.

## shader-language-server 1.6.2

- Component: `ShaderTools.VisualStudio/Server/shader-language-server.exe`
- Project: [antaalt/shader-sense](https://github.com/antaalt/shader-sense)
- Release: `v1.6.2` (`9d11fa8f6ca114819cedcc3beb5d5121666689f1`)
- License: MIT, copyright (c) 2024 antaalt
- License file: `licenses/shader-sense-LICENSE.txt`

The executable and both DXC libraries come from the official Windows x64
release archive. Its SHA-256 and the individual file hashes are pinned in
`scripts/server-release.psd1`.

The server's dependency notices are reproduced in
`licenses/ShaderLanguageServer-ThirdPartyNotices.txt`, including native glslang,
Naga, tree-sitter and the shader grammars. Its inventory covers 149 packages
from the pinned Cargo.lock dependency closure (including build and optional
platform dependencies), with source URLs and verified archive checksums.

## Shader Validator TextMate grammars

- Components: `ShaderTools.VisualStudio/Grammars/glsl.tmLanguage.json`,
  `ShaderTools.VisualStudio/Grammars/hlsl.tmLanguage.json`, and
  `ShaderTools.VisualStudio/Grammars/wgsl.tmLanguage.json`
- Project: [antaalt/shader-validator](https://github.com/antaalt/shader-validator)
- Source revision: `41fd82d36979c7f9fb72b23e1ccf82a6f5978957`
- Authors identified by the grammar metadata: antaalt; the WGSL grammar also
  identifies PolyMeilex
- License: MIT, copyright (c) 2024 antaalt
- License file: `licenses/shader-validator-LICENSE.txt`

The distributed copies only change their `fileTypes` lists to cover the shader
file extensions supported by Shader Tools.

## DirectX Shader Compiler 1.8.2405

- Components: `ShaderTools.VisualStudio/Server/dxcompiler.dll` and `ShaderTools.VisualStudio/Server/dxil.dll`
- Project: [microsoft/DirectXShaderCompiler](https://github.com/microsoft/DirectXShaderCompiler)
- Release: `v1.8.2405`, selected by the shader-language-server `v1.6.2`
  Windows release workflow
- License file: `licenses/DirectXShaderCompiler-LICENSE.txt`
- Upstream third-party notices:
  `licenses/DirectXShaderCompiler-ThirdPartyNotices.txt`

The DLLs are redistributed without modification as part of the pinned upstream
shader-language-server release asset.

## Newtonsoft.Json 13.0.3

- Component: `Bridge/Newtonsoft.Json.dll`
- Project: [JamesNK/Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)
- License: MIT, copyright (c) 2007 James Newton-King
- License file: `licenses/Newtonsoft.Json-LICENSE.txt`
