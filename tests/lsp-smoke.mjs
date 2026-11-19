#!/usr/bin/env node

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  watch,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { fileURLToPath, pathToFileURL } from "node:url";

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = path.resolve(SCRIPT_DIR, "..");
const DEFAULT_BRIDGE = path.join(
  REPOSITORY_ROOT,
  "ShaderTools.Bridge",
  "bin",
  "Release",
  "net472",
  "ShaderTools.Bridge.exe",
);
const DEFAULT_SERVER = path.join(
  REPOSITORY_ROOT,
  "ShaderTools.VisualStudio",
  "Server",
  "shader-language-server.exe",
);
const REQUEST_TIMEOUT_MS = 15_000;
const DIAGNOSTIC_TIMEOUT_MS = 20_000;
const EXIT_TIMEOUT_MS = 7_000;

function parseArguments(argv) {
  const options = {
    bridge: DEFAULT_BRIDGE,
    server: DEFAULT_SERVER,
    serverOnly: false,
    settings: undefined,
    parent: undefined,
    formatDirectory: undefined,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === "--server-only") {
      options.serverOnly = true;
      continue;
    }
    if (argument === "--help" || argument === "-h") {
      console.log(`Usage: node tests/lsp-smoke.mjs [options]

Options:
  --bridge PATH           Bridge executable (default: Release/net472 build)
  --server PATH           shader-language-server executable (default: bundled server)
  --settings PATH         Optional user settings JSON passed to the bridge
  --parent PID            Optional parent process ID passed to the bridge
  --format-directory DIR  Directory containing clang-format
  --server-only           Characterize the backend directly, bypassing the bridge
  --help                  Show this help`);
      process.exit(0);
    }
    if (!["--bridge", "--server", "--settings", "--parent", "--format-directory"].includes(argument)) {
      throw new Error(`Unknown argument: ${argument}`);
    }
    if (index + 1 >= argv.length) {
      throw new Error(`${argument} requires a value`);
    }
    const key = {
      "--bridge": "bridge",
      "--server": "server",
      "--settings": "settings",
      "--parent": "parent",
      "--format-directory": "formatDirectory",
    }[argument];
    const value = argv[++index];
    options[key] = key === "parent" ? value : path.resolve(value);
  }
  return options;
}

function findClangFormatDirectory() {
  const executable = process.platform === "win32" ? "clang-format.exe" : "clang-format";
  const candidates = [];
  for (const item of (process.env.PATH ?? "").split(path.delimiter)) {
    if (item) candidates.push(item.replace(/^"|"$/g, ""));
  }

  if (process.platform === "win32") {
    const programFiles = process.env.ProgramFiles ?? "C:\\Program Files";
    candidates.push(path.join(programFiles, "LLVM", "bin"));
    const visualStudioRoot = path.join(programFiles, "Microsoft Visual Studio");
    if (existsSync(visualStudioRoot)) {
      for (const version of safeDirectories(visualStudioRoot)) {
        for (const edition of safeDirectories(path.join(visualStudioRoot, version))) {
          candidates.push(
            path.join(visualStudioRoot, version, edition, "VC", "Tools", "Llvm", "x64", "bin"),
            path.join(visualStudioRoot, version, edition, "VC", "Tools", "Llvm", "bin"),
          );
        }
      }
    }
  }

  return candidates.find((directory) => existsSync(path.join(directory, executable)));
}

function safeDirectories(directory) {
  try {
    return readdirSync(directory).filter((entry) => {
      try {
        return statSync(path.join(directory, entry)).isDirectory();
      } catch {
        return false;
      }
    });
  } catch {
    return [];
  }
}

function timeoutPromise(milliseconds, description) {
  let timer;
  const promise = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`Timed out after ${milliseconds} ms: ${description}`)), milliseconds);
  });
  return { promise, cancel: () => clearTimeout(timer) };
}

async function runChildProcess(command, args, spawnOptions, milliseconds, description) {
  const child = spawn(command, args, {
    ...spawnOptions,
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
  });
  let stdout = "";
  let stderr = "";
  child.stdout.on("data", (chunk) => { stdout += chunk.toString("utf8"); });
  child.stderr.on("data", (chunk) => { stderr += chunk.toString("utf8"); });
  const completed = new Promise((resolve, reject) => {
    child.once("error", reject);
    child.once("exit", (code, signal) => resolve({ code, signal, stdout, stderr }));
  });
  const timeout = timeoutPromise(milliseconds, description);
  try {
    return await Promise.race([completed, timeout.promise]);
  } catch (error) {
    if (!child.killed) child.kill();
    throw error;
  } finally {
    timeout.cancel();
  }
}

function findFrameworkCompiler() {
  if (process.platform !== "win32") return undefined;
  const windows = process.env.WINDIR ?? "C:\\Windows";
  return [
    path.join(windows, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
    path.join(windows, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe"),
  ].find(existsSync);
}

async function compileBlockingClangFormat(directory) {
  const compiler = findFrameworkCompiler();
  if (!compiler) throw new SkipTest("the .NET Framework C# compiler was not found");
  mkdirSync(directory, { recursive: true });
  const sourcePath = path.join(directory, "BlockingClangFormat.cs");
  const executablePath = path.join(directory, "clang-format.exe");
  writeFileSync(sourcePath, `using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class BlockingClangFormat
{
    private static int Main(string[] args)
    {
        if (Array.Exists(args, value => value == "--version"))
        {
            Console.WriteLine("clang-format version Shader Tools lifecycle test");
            return 0;
        }

        string marker = Environment.GetEnvironmentVariable("SHADER_TOOLS_FORMATTER_MARKER");
        if (String.IsNullOrEmpty(marker)) return 2;
        File.WriteAllText(marker, Process.GetCurrentProcess().Id.ToString());
        using (var gate = new ManualResetEvent(false)) gate.WaitOne();
        return 0;
    }
}
`);
  const result = await runChildProcess(
    compiler,
    ["/nologo", "/target:exe", `/out:${executablePath}`, sourcePath],
    { cwd: directory },
    15_000,
    "blocking clang-format fixture compilation",
  );
  assert.equal(result.code, 0, `cannot compile blocking clang-format fixture:\n${result.stdout}${result.stderr}`);
  assert.ok(existsSync(executablePath), "blocking clang-format fixture executable was not created");
  return executablePath;
}

function waitForPidMarker(markerPath, milliseconds) {
  const readMarker = () => {
    try {
      const pid = Number(readFileSync(markerPath, "utf8").trim());
      return Number.isInteger(pid) && pid > 0 ? pid : undefined;
    } catch {
      return undefined;
    }
  };
  const existing = readMarker();
  if (existing) return Promise.resolve(existing);

  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      watcher.close();
      reject(new Error(`Timed out after ${milliseconds} ms waiting for formatter PID marker`));
    }, milliseconds);
    const watcher = watch(path.dirname(markerPath), () => {
      const pid = readMarker();
      if (!pid) return;
      clearTimeout(timer);
      watcher.close();
      resolve(pid);
    });
    watcher.once("error", (error) => {
      clearTimeout(timer);
      watcher.close();
      reject(error);
    });
  });
}

async function waitForPidExit(pid, milliseconds) {
  assert.ok(Number.isInteger(pid) && pid > 0, `invalid owned process PID ${pid}`);
  const script = `try { $target = [System.Diagnostics.Process]::GetProcessById(${pid}); if (-not $target.WaitForExit(${milliseconds})) { exit 3 } } catch [System.ArgumentException] { exit 0 }`;
  const result = await runChildProcess(
    "powershell.exe",
    ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
    {},
    milliseconds + 2_000,
    `owned PID ${pid} exit`,
  );
  assert.equal(result.code, 0, `owned PID ${pid} did not exit within ${milliseconds} ms: ${result.stderr}`);
}

class LspConnection {
  constructor(command, args, spawnOptions) {
    this.command = command;
    this.args = args;
    this.nextId = 1;
    this.sequence = 0;
    this.buffer = Buffer.alloc(0);
    this.pending = new Map();
    this.notifications = [];
    this.notificationWaiters = new Set();
    this.stderr = "";
    this.serverPids = new Set();
    this.exitResult = undefined;

    this.child = spawn(command, args, {
      ...spawnOptions,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.exitPromise = new Promise((resolve) => {
      this.child.once("exit", (code, signal) => {
        this.exitResult = { code, signal };
        const error = new Error(
          `${path.basename(command)} exited before completing LSP work (code ${code}, signal ${signal}).\n${this.stderr}`,
        );
        for (const pending of this.pending.values()) pending.reject(error);
        this.pending.clear();
        for (const waiter of this.notificationWaiters) waiter.reject(error);
        this.notificationWaiters.clear();
        resolve(this.exitResult);
      });
    });
    this.child.stdout.on("data", (chunk) => this.onStdout(chunk));
    this.child.stderr.on("data", (chunk) => {
      const text = chunk.toString("utf8");
      this.stderr = (this.stderr + text).slice(-64 * 1024);
      for (const match of this.stderr.matchAll(/shader-language-server[^\r\n]*\(PID (\d+)\)/gi)) {
        this.serverPids.add(Number(match[1]));
      }
    });
    this.child.once("error", (error) => {
      this.stderr += `${error.stack ?? error}\n`;
    });
  }

  marker() {
    return this.sequence;
  }

  send(message) {
    if (!this.child.stdin.writable) {
      throw new Error(`Cannot write to ${path.basename(this.command)}; stdin is closed.`);
    }
    const body = Buffer.from(JSON.stringify(message), "utf8");
    const header = Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, "ascii");
    this.child.stdin.write(Buffer.concat([header, body]));
  }

  notify(method, params = {}) {
    this.send({ jsonrpc: "2.0", method, params });
  }

  request(method, params = {}, milliseconds = REQUEST_TIMEOUT_MS) {
    const id = this.nextId++;
    const timeout = timeoutPromise(milliseconds, `${method} response`);
    const response = new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.send({ jsonrpc: "2.0", id, method, params });
    });
    return Promise.race([response, timeout.promise]).finally(() => {
      timeout.cancel();
      this.pending.delete(id);
    });
  }

  waitForNotification(method, predicate, afterSequence, milliseconds = REQUEST_TIMEOUT_MS) {
    const existing = this.notifications.find(
      (entry) => entry.sequence > afterSequence && entry.message.method === method && predicate(entry.message.params),
    );
    if (existing) return Promise.resolve(existing.message.params);

    const timeout = timeoutPromise(milliseconds, `${method} notification`);
    let waiter;
    const result = new Promise((resolve, reject) => {
      waiter = { method, predicate, afterSequence, resolve, reject };
      this.notificationWaiters.add(waiter);
    });
    return Promise.race([result, timeout.promise]).finally(() => {
      timeout.cancel();
      this.notificationWaiters.delete(waiter);
    });
  }

  onStdout(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (true) {
      const headerEnd = this.buffer.indexOf("\r\n\r\n");
      if (headerEnd < 0) return;
      const header = this.buffer.subarray(0, headerEnd).toString("ascii");
      const lengths = [...header.matchAll(/^Content-Length:\s*(\d+)\s*$/gim)];
      if (lengths.length !== 1) {
        this.failProtocol(new Error(`Invalid LSP header from server: ${JSON.stringify(header)}`));
        return;
      }
      const bodyLength = Number(lengths[0][1]);
      const messageEnd = headerEnd + 4 + bodyLength;
      if (this.buffer.length < messageEnd) return;
      const body = this.buffer.subarray(headerEnd + 4, messageEnd);
      this.buffer = this.buffer.subarray(messageEnd);
      try {
        this.onMessage(JSON.parse(body.toString("utf8")));
      } catch (error) {
        this.failProtocol(new Error(`Invalid JSON from server: ${error.message}`));
        return;
      }
    }
  }

  failProtocol(error) {
    for (const pending of this.pending.values()) pending.reject(error);
    this.pending.clear();
    for (const waiter of this.notificationWaiters) waiter.reject(error);
    this.notificationWaiters.clear();
    if (!this.child.killed) this.child.kill();
  }

  onMessage(message) {
    if (Object.hasOwn(message, "id") && !Object.hasOwn(message, "method")) {
      const pending = this.pending.get(message.id);
      if (!pending) return;
      if (message.error) {
        pending.reject(new Error(`LSP ${message.error.code}: ${message.error.message}`));
      } else {
        pending.resolve(message.result);
      }
      return;
    }

    if (Object.hasOwn(message, "id") && message.method) {
      this.handleServerRequest(message);
      return;
    }

    if (message.method) {
      const entry = { sequence: ++this.sequence, message };
      this.notifications.push(entry);
      if (this.notifications.length > 500) this.notifications.shift();
      for (const waiter of [...this.notificationWaiters]) {
        if (
          entry.sequence > waiter.afterSequence &&
          message.method === waiter.method &&
          waiter.predicate(message.params)
        ) {
          this.notificationWaiters.delete(waiter);
          waiter.resolve(message.params);
        }
      }
    }
  }

  handleServerRequest(message) {
    let result;
    switch (message.method) {
      case "workspace/configuration":
        result = (message.params?.items ?? []).map((item) => configurationForSection(item.section));
        break;
      case "textDocument/shaderVariant":
        result = { shaderVariant: activeVariant };
        break;
      case "window/workDoneProgress/create":
        result = null;
        break;
      default:
        this.send({
          jsonrpc: "2.0",
          id: message.id,
          error: { code: -32601, message: `Test client does not handle ${message.method}` },
        });
        return;
    }
    this.send({ jsonrpc: "2.0", id: message.id, result });
  }

  async waitForExit(milliseconds = EXIT_TIMEOUT_MS) {
    if (this.exitResult) return this.exitResult;
    const timeout = timeoutPromise(milliseconds, `${path.basename(this.command)} process exit`);
    try {
      return await Promise.race([this.exitPromise, timeout.promise]);
    } finally {
      timeout.cancel();
    }
  }

  terminate() {
    if (!this.child.killed && !this.exitResult) this.child.kill();
  }
}

function configurationForSection(section) {
  if (!section || section === "shader-validator") return serverConfiguration;
  if (!section.startsWith("shader-validator.")) return null;
  let value = serverConfiguration;
  for (const part of section.slice("shader-validator.".length).split(".")) value = value?.[part];
  return value ?? null;
}

function uriFor(filePath) {
  return pathToFileURL(filePath).href;
}

function positionAt(text, needle, offset = 0) {
  const absolute = text.indexOf(needle);
  assert.notEqual(absolute, -1, `Could not find ${JSON.stringify(needle)} in fixture text`);
  const target = absolute + offset;
  const prefix = text.slice(0, target);
  const lines = prefix.split("\n");
  return { line: lines.length - 1, character: lines.at(-1).replace(/\r$/, "").length };
}

function completionItems(response) {
  if (Array.isArray(response)) return response;
  return response?.items ?? [];
}

function diagnosticItems(response) {
  if (!response) return [];
  if (Array.isArray(response.items)) return response.items;
  if (Array.isArray(response.relatedDocuments?.items)) return response.relatedDocuments.items;
  return [];
}

function flattenDocumentSymbols(symbols) {
  const flattened = [];
  for (const symbol of symbols ?? []) {
    flattened.push(symbol);
    if (symbol.children) flattened.push(...flattenDocumentSymbols(symbol.children));
  }
  return flattened;
}

function locationUris(response) {
  if (!response) return [];
  const locations = Array.isArray(response) ? response : [response];
  return locations.map((location) => location.targetUri ?? location.uri).filter(Boolean);
}

function isProcessAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error.code === "ESRCH") return false;
    if (error.code === "EPERM") return true;
    throw error;
  }
}

const options = parseArguments(process.argv.slice(2));
if (!existsSync(options.server)) throw new Error(`Server executable not found: ${options.server}`);
if (!options.serverOnly && !existsSync(options.bridge)) throw new Error(`Bridge executable not found: ${options.bridge}`);
if (options.settings && !existsSync(options.settings)) throw new Error(`Settings file not found: ${options.settings}`);

const clangFormatDirectory = options.formatDirectory ?? findClangFormatDirectory();
const temporaryRoot = mkdtempSync(path.join(os.tmpdir(), "Shader Tools Ω workspace "));
const shaderDirectory = path.join(temporaryRoot, "Шейдеры with spaces");
const includeDirectory = path.join(temporaryRoot, "includes with spaces");
mkdirSync(shaderDirectory, { recursive: true });
mkdirSync(includeDirectory, { recursive: true });

const configurationPath = path.join(temporaryRoot, ".shadertools.json");
const sharedPath = path.join(includeDirectory, "shared ü.glsl");
const sharedHlslPath = path.join(includeDirectory, "shared ü.hlsli");
const featurePath = path.join(shaderDirectory, "features ü.frag.glsl");
const diagnosticPath = path.join(shaderDirectory, "unicode edit ü.frag.glsl");
const hlslPath = path.join(shaderDirectory, "compute shader ü.frag.hlsl");

const serverConfiguration = {
  includes: [includeDirectory],
  defines: {},
  validate: true,
  symbols: true,
  symbolDiagnostics: false,
  validateConfig: true,
  automaticVariantDiscovery: false,
  experimentalMacroExpansion: false,
  severity: "hint",
  trace: { server: "off" },
  hlsl: {
    enabled: true,
    shaderModel: "ShaderModel6_8",
    version: "V2021",
    enable16bitTypes: false,
    spirv: false,
  },
  glsl: {
    enabled: true,
    targetClient: "Vulkan1_3",
    spirvVersion: "SPIRV1_6",
  },
  wgsl: { enabled: false },
};

writeFileSync(configurationPath, `${JSON.stringify({ ...serverConfiguration, includes: ["includes with spaces"] }, null, 2)}\n`);
writeFileSync(
  sharedPath,
  `#ifndef SHARED_COLOR_INCLUDED
#define SHARED_COLOR_INCLUDED
vec4 sharedColor() {
    return vec4(0.25, 0.5, 0.75, 1.0);
}
#endif
`,
);

const featureText = `#version 450
#extension GL_GOOGLE_include_directive : require
#include "shared ü.glsl"

layout(location = 0) out vec4 outColor;
vec3 localTint = vec3(0.5, 0.75, 1.0);

float computeLocal(float value) {
    return value * 2.0;
}

void helperScope() {
    if (localTint.x > 0.0) {
        outColor = vec4(localTint, 1.0);
    }
}

void main() {
    float sampleValue = computeLocal(1.0);
    vec3 normalizedTint = normalize(localTint);
    outColor = sharedColor() + vec4(normalizedTint * sampleValue, 0.0);
}
`;
writeFileSync(featurePath, featureText);

const diagnosticInvalidText = `#version 450
layout(location = 0) out vec4 outColor;
void main() { /* 😀 юникод */ outColor = vec4(1.0) }
`;
writeFileSync(diagnosticPath, diagnosticInvalidText);

writeFileSync(sharedHlslPath, "float4 includedTint() { return float4(1.0, 0.8, 0.6, 1.0); }\n");
const hlslInvalidText = `#include "shared ü.hlsli"
float4 localShade(float2 uv) {
    return float4(uv, 0.25, 1.0)
}

float4 main(float4 position : SV_Position) : SV_Target0 {
    return localShade(position.xy) * includedTint();
}
`;
const hlslValidText = hlslInvalidText.replace("float4(uv, 0.25, 1.0)\n", "float4(uv, 0.25, 1.0);\n");
writeFileSync(hlslPath, hlslInvalidText);

let activeVariant = null;
let connection;
let initialized = false;
let initializeCapabilities = {};
let shutdownSent = false;
let passed = 0;
let failed = 0;
let skipped = 0;
const failures = [];

class SkipTest extends Error {}

async function runTest(name, action) {
  try {
    await action();
    passed += 1;
    console.log(`PASS ${name}`);
  } catch (error) {
    if (error instanceof SkipTest) {
      skipped += 1;
      console.log(`SKIP ${name}: ${error.message}`);
      return;
    }
    failed += 1;
    failures.push({ name, error });
    console.error(`FAIL ${name}: ${error.message}`);
  }
}

function openDocument(filePath, languageId, version, text) {
  const effectiveLanguageId = options.serverOnly && languageId.startsWith("shader-tools-")
    ? languageId.slice("shader-tools-".length)
    : languageId;
  connection.notify("textDocument/didOpen", {
    textDocument: { uri: uriFor(filePath), languageId: effectiveLanguageId, version, text },
  });
}

function closeDocument(filePath) {
  connection.notify("textDocument/didClose", { textDocument: { uri: uriFor(filePath) } });
}

async function pullDiagnostics(filePath) {
  const response = await connection.request("textDocument/diagnostic", {
    textDocument: { uri: uriFor(filePath) },
    identifier: null,
    previousResultId: null,
  });
  return diagnosticItems(response);
}

async function main() {
  const environment = { ...process.env };
  if (clangFormatDirectory) {
    environment.PATH = `${clangFormatDirectory}${path.delimiter}${environment.PATH ?? ""}`;
  }

  let command;
  let args;
  if (options.serverOnly) {
    command = options.server;
    args = [
      "--stdio",
      "--glsl",
      "--hlsl",
      "--cwd",
      temporaryRoot,
      "--config",
      JSON.stringify({ ...serverConfiguration, hlsl: { ...serverConfiguration.hlsl, enabled: undefined }, glsl: { ...serverConfiguration.glsl, enabled: undefined }, wgsl: undefined }),
    ];
  } else {
    command = options.bridge;
    args = ["--server", options.server, "--workspace", temporaryRoot];
    if (options.settings) args.push("--settings", options.settings);
    if (options.parent) args.push("--parent", options.parent);
    if (clangFormatDirectory) args.push("--format-directory", clangFormatDirectory);
  }

  console.log(`MODE ${options.serverOnly ? "server-only" : "bridge"}`);
  console.log(`WORKSPACE ${temporaryRoot}`);
  console.log(`SERVER ${options.server}`);
  console.log(`CLANG_FORMAT ${clangFormatDirectory ?? "not found"}`);
  connection = new LspConnection(command, args, { cwd: temporaryRoot, env: environment });

  await runTest("initialize advertises GLSL/HLSL language features", async () => {
    const rootUri = uriFor(temporaryRoot);
    const result = await connection.request("initialize", {
      processId: process.pid,
      clientInfo: { name: "shader-tools-lsp-smoke", version: "1" },
      rootPath: temporaryRoot,
      rootUri,
      workspaceFolders: [{ uri: rootUri, name: path.basename(temporaryRoot) }],
      capabilities: {
        general: { positionEncodings: ["utf-16"] },
        workspace: { configuration: false, workspaceFolders: true },
        textDocument: {
          completion: { completionItem: { labelDetailsSupport: true } },
          publishDiagnostics: { relatedInformation: true, tagSupport: { valueSet: [1, 2] } },
          foldingRange: { lineFoldingOnly: true },
        },
      },
    });
    const capabilities = result?.capabilities ?? {};
    initializeCapabilities = capabilities;
    assert.ok(capabilities.textDocumentSync, "incremental synchronization was not advertised");
    assert.ok(capabilities.completionProvider, "completion was not advertised");
    assert.ok(capabilities.hoverProvider, "hover was not advertised");
    assert.ok(capabilities.signatureHelpProvider, "signature help was not advertised");
    assert.ok(capabilities.definitionProvider, "definition was not advertised");
    assert.ok(capabilities.documentSymbolProvider, "document symbols were not advertised");
    assert.ok(capabilities.workspaceSymbolProvider, "workspace symbols were not advertised");
    assert.ok(capabilities.foldingRangeProvider, "folding ranges were not advertised");
    connection.notify("initialized", {});
    initialized = true;
  });

  await runTest("GLSL diagnostics clear after a UTF-16 incremental edit", async () => {
    const uri = uriFor(diagnosticPath);
    let marker = connection.marker();
    openDocument(diagnosticPath, "shader-tools-glsl", 1, diagnosticInvalidText);
    const invalid = await connection.waitForNotification(
      "textDocument/publishDiagnostics",
      (params) => params.uri === uri && params.diagnostics.length > 0,
      marker,
      DIAGNOSTIC_TIMEOUT_MS,
    ).catch((error) => {
      throw new Error(`waiting for invalid GLSL diagnostics: ${error.message}`);
    });
    assert.ok(invalid.diagnostics.some((diagnostic) => diagnostic.severity === 1), "expected a GLSL compiler error");
    if (!options.serverOnly) {
      const mirrored = await connection.waitForNotification("shaderTools/diagnostics", (params) => params.uri === uri,
        marker, DIAGNOSTIC_TIMEOUT_MS);
      assert.deepEqual(mirrored, invalid, "native editor diagnostic mirror changed the UTF-16 payload");
    }

    const line = diagnosticInvalidText.split("\n")[2];
    const insertionCharacter = line.lastIndexOf(" }") + 1;
    assert.ok(insertionCharacter > 0, "fixture insertion point was not found");
    assert.notEqual(
      insertionCharacter,
      Buffer.byteLength(line.slice(0, insertionCharacter), "utf8"),
      "fixture must exercise a non-ASCII UTF-16 position",
    );
    marker = connection.marker();
    connection.notify("textDocument/didChange", {
      textDocument: { uri, version: 2 },
      contentChanges: [
        {
          range: {
            start: { line: 2, character: insertionCharacter },
            end: { line: 2, character: insertionCharacter },
          },
          rangeLength: 0,
          text: ";",
        },
      ],
    });
    const valid = await connection.waitForNotification(
      "textDocument/publishDiagnostics",
      (params) => params.uri === uri && params.diagnostics.length === 0,
      marker,
      DIAGNOSTIC_TIMEOUT_MS,
    ).catch((error) => {
      const observed = connection.notifications
        .filter((entry) => entry.sequence > marker && entry.message.method === "textDocument/publishDiagnostics")
        .map((entry) => entry.message.params);
      throw new Error(`waiting for cleared GLSL diagnostics: ${error.message}; observed ${JSON.stringify(observed)}`);
    });
    assert.deepEqual(valid.diagnostics, []);
    closeDocument(diagnosticPath);
  });

  await runTest("GLSL completion contains local symbols and intrinsics", async () => {
    openDocument(featurePath, "shader-tools-glsl", 1, featureText);
    const response = await connection.request("textDocument/completion", {
      textDocument: { uri: uriFor(featurePath) },
      position: positionAt(featureText, "normalizedTint =", 0),
      context: { triggerKind: 1 },
    });
    const labels = new Set(completionItems(response).map((item) => item.label));
    assert.ok(labels.has("localTint"), `localTint missing from ${labels.size} completion items`);
    assert.ok(labels.has("normalize"), `normalize intrinsic missing from ${labels.size} completion items`);
  });

  await runTest("GLSL hover and signature help resolve local functions", async () => {
    const hover = await connection.request("textDocument/hover", {
      textDocument: { uri: uriFor(featurePath) },
      position: positionAt(featureText, "computeLocal(1.0)", 2),
    });
    const hoverText = JSON.stringify(hover?.contents ?? hover);
    assert.match(hoverText, /computeLocal/);

    const signature = await connection.request("textDocument/signatureHelp", {
      textDocument: { uri: uriFor(featurePath) },
      position: positionAt(featureText, "computeLocal(1.0)", "computeLocal(".length),
      context: { triggerKind: 1, isRetrigger: false },
    });
    assert.ok(signature?.signatures?.length > 0, "signature help returned no signatures");
    assert.ok(signature.signatures.some((item) => item.label.includes("computeLocal")), "local function signature missing");
  });

  await runTest("parameter hints update call sites after signature edits and survive unfinished code", async () => {
    const hintsPath = path.join(shaderDirectory, "parameter hints.frag.glsl");
    const original = [
      "#version 450",
      "float scale(float amount) { return amount; }",
      "void main() {",
      "    float first = scale(1.0);",
      "    float second = scale(2.0);",
      "}",
      "",
    ].join("\n");
    writeFileSync(hintsPath, original);
    openDocument(hintsPath, "shader-tools-glsl", 1, original);
    const uri = uriFor(hintsPath);
    const readHints = async (text) => {
      const lines = text.split("\n");
      const hints = await connection.request("textDocument/inlayHint", {
        textDocument: { uri },
        range: { start: { line: 0, character: 0 }, end: { line: lines.length - 1, character: lines.at(-1).length } },
      });
      assert.ok(Array.isArray(hints), "expected an inlay hint response");
      return hints.filter((hint) => hint.position.line === 3 || hint.position.line === 4)
        .map((hint) => typeof hint.label === "string" ? hint.label : hint.label.map((part) => part.value).join(""));
    };
    assert.deepEqual(await readHints(original), ["amount:", "amount:"]);

    const renamed = original.replaceAll("amount", "value");
    connection.notify("textDocument/didChange", {
      textDocument: { uri, version: 2 },
      contentChanges: [{
        range: { start: { line: 1, character: 0 }, end: { line: 1, character: original.split("\n")[1].length } },
        text: "float scale(float value) { return value; }",
      }],
    });
    assert.deepEqual(await readHints(renamed), ["value:", "value:"]);

    const unfinished = renamed.replace("\n}\n", "\n    float editing =\n}\n");
    connection.notify("textDocument/didChange", {
      textDocument: { uri, version: 3 },
      contentChanges: [{ text: unfinished }],
    });
    assert.deepEqual(await readHints(unfinished), ["value:", "value:"]);
    closeDocument(hintsPath);
  });

  await runTest("completion supplies parameter signatures while a call is still open", async () => {
    const partialPath = path.join(shaderDirectory, "partial arguments.frag.glsl");
    const text = "#version 450\nfloat custom(float first, int second) { return first; }\nvoid main() {\ncustom(3.3, 2\n}\n";
    writeFileSync(partialPath, text);
    openDocument(partialPath, "shader-tools-glsl", 1, text);
    const response = await connection.request("textDocument/completion", {
      textDocument: { uri: uriFor(partialPath) },
      position: { line: text.split("\n").length - 1, character: 0 },
      context: { triggerKind: 1 },
    });
    const items = completionItems(response);
    const declaration = (name) => {
      const documentation = items.find((item) => item.label === name)?.documentation;
      const markdown = typeof documentation === "string" ? documentation : documentation?.value;
      assert.ok(markdown?.startsWith("```"), `${name} has no fenced signature documentation`);
      return markdown.slice(markdown.indexOf("\n") + 1, markdown.indexOf("```", 3));
    };
    assert.match(declaration("custom"), /custom\(float first, int second\)/);
    assert.match(declaration("mix"), /mix\([^)]*\bx\b[^)]*\by\b[^)]*\ba\b/);
    closeDocument(partialPath);
  });

  await runTest("goto definition resolves a symbol declared in a configured include", async () => {
    const response = await connection.request("textDocument/definition", {
      textDocument: { uri: uriFor(featurePath) },
      position: positionAt(featureText, "sharedColor() +", 2),
    });
    const targets = locationUris(response);
    assert.ok(targets.length > 0, "definition returned no locations");
    assert.ok(
      targets.some((target) => decodeURIComponent(target).toLowerCase() === decodeURIComponent(uriFor(sharedPath)).toLowerCase()),
      `definition did not target ${uriFor(sharedPath)}: ${targets.join(", ")}`,
    );
  });

  await runTest("goto definition opens the quoted GLSL include path", async () => {
    const response = await connection.request("textDocument/definition", {
      textDocument: { uri: uriFor(featurePath) },
      position: positionAt(featureText, "shared ü.glsl", 8),
    });
    assert.ok(locationUris(response).some((target) =>
      decodeURIComponent(target).toLowerCase() === decodeURIComponent(uriFor(sharedPath)).toLowerCase()),
      "the include filename did not resolve to its header");
  });

  await runTest("document and workspace symbols include shader declarations", async () => {
    const documentSymbols = await connection.request("textDocument/documentSymbol", {
      textDocument: { uri: uriFor(featurePath) },
    });
    const documentNames = new Set(flattenDocumentSymbols(documentSymbols).map((symbol) => symbol.name));
    assert.ok(documentNames.has("main"), "main missing from document symbols");
    assert.ok(documentNames.has("computeLocal"), "computeLocal missing from document symbols");

    const workspaceSymbols = await connection.request("workspace/symbol", { query: "computeLocal" });
    assert.ok(workspaceSymbols?.some((symbol) => symbol.name === "computeLocal"), "computeLocal missing from workspace symbols");
  });

  await runTest("large GLSL document with includes and branches answers interactive requests", async () => {
    const largePath = path.join(shaderDirectory, "large.frag");
    const functions = Array.from({ length: 120 }, (_, i) => `float helper${i}(float amount)\n{\n#if LARGE_ENABLED\n    return amount * 2.0;\n#else\n    return amount;\n#endif\n}\n`).join("\n");
    const text = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#include "shared ü.glsl"\nlayout(location = 0) out vec4 largeColor;\n' + functions + '\nvoid main() { largeColor = vec4(sharedColor(), helper119(1.0)); }\n';
    writeFileSync(largePath, text);
    openDocument(largePath, "shader-tools-glsl", 1, text);
    const started = Date.now();
    const [definition, signature, symbols] = await Promise.all([
      connection.request("textDocument/definition", { textDocument: { uri: uriFor(largePath) }, position: positionAt(text, "sharedColor(),", 2) }),
      connection.request("textDocument/signatureHelp", { textDocument: { uri: uriFor(largePath) }, position: positionAt(text, "helper119(1.0)", "helper119(".length), context: { triggerKind: 1, isRetrigger: false } }),
      connection.request("textDocument/documentSymbol", { textDocument: { uri: uriFor(largePath) } }),
    ]);
    assert.ok(locationUris(definition).some(uri => decodeURIComponent(uri).toLowerCase() === decodeURIComponent(uriFor(sharedPath)).toLowerCase()));
    assert.ok(signature?.signatures?.some(item => item.label.includes("helper119")));
    assert.ok(symbols.some(item => item.name === "helper119"));
    console.log(`INFO ${text.split("\n").length} GLSL lines: definition/signature/symbols completed in ${Date.now() - started} ms`);
    closeDocument(largePath);
  });

  await runTest("include inspection uses the analyzed context and current unsaved document", async () => {
    if (options.serverOnly) throw new SkipTest("inspection is provided by the Bridge");
    const context = await connection.request("shaderTools/compilationContext", { textDocument: { uri: uriFor(featurePath) } });
    assert.equal(context.documentVersion, 1);
    const report = await connection.request("shaderTools/preprocessor", { textDocument: { uri: uriFor(featurePath) }, context });
    assert.equal(report.version, 1);
    assert.ok(report.includes.some(edge => edge.status === "resolved" && edge.target.toLowerCase() === sharedPath.toLowerCase()));
    const staleContext = { ...context, documentVersion: 0 };
    await assert.rejects(connection.request("shaderTools/preprocessor", { textDocument: { uri: uriFor(featurePath) }, context: staleContext }), /changed/);
  });

  await runTest("include diagnostics offer a version-checked configuration action", async () => {
    if (options.serverOnly) throw new SkipTest("configuration action is provided by the Bridge");
    const actionPath = path.join(shaderDirectory, "missing-include.frag");
    const actionText = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#include "unavailable-header.glsl"\nvoid main() {}\n';
    writeFileSync(actionPath, actionText);
    const uri = uriFor(actionPath);
    const marker = connection.marker();
    openDocument(actionPath, "shader-tools-glsl", 1, actionText);
    await connection.waitForNotification("textDocument/publishDiagnostics", params => params.uri === uri && params.diagnostics.length > 0, marker);
    const diagnostics = await pullDiagnostics(actionPath);
    assert.ok(diagnostics.some(item => item.severity === 1 && item.range.start.line === 2));
    const actions = await connection.request("textDocument/codeAction", { textDocument: { uri }, range: { start: { line: 2, character: 0 }, end: { line: 2, character: 35 } }, context: { diagnostics, only: ["quickfix"] } });
    assert.equal(actions.length, 1);
    assert.equal(actions[0].command.command, "shaderTools.openConfiguration");
    const commandMarker = connection.marker();
    await connection.request("workspace/executeCommand", actions[0].command);
    const opened = await connection.waitForNotification("shaderTools/openConfiguration", () => true, commandMarker);
    assert.equal(decodeURIComponent(opened.uri), decodeURIComponent(uriFor(configurationPath)));
    connection.notify("textDocument/didChange", { textDocument: { uri, version: 2 }, contentChanges: [{ text: "#version 450\nvoid main() {}\n" }] });
    await assert.rejects(connection.request("workspace/executeCommand", actions[0].command), /changed/);
    closeDocument(actionPath);
  });

  await runTest("parent workspace resolves nested GLSL and HLSL project settings independently", async () => {
    if (options.serverOnly) throw new SkipTest("configuration discovery is provided by the Bridge");
    const projects = ["project A", "project B"].map(name => path.join(temporaryRoot, "examples", name));
    const fixture = path.join(REPOSITORY_ROOT, "examples", "BasicShaders");
    const files = [".shadertools.json", "include/colors.glsl", "include/colors.hlsli", "shaders/triangle.frag", "shaders/triangle.ps.hlsl"];
    const openedDocuments = [];
    try {
      for (const project of projects) {
        for (const file of files) {
          const target = path.join(project, file);
          mkdirSync(path.dirname(target), { recursive: true });
          writeFileSync(target, readFileSync(path.join(fixture, file)));
        }
        if (project === projects[0]) {
          const config = path.join(project, ".shadertools.json");
          const settings = JSON.parse(readFileSync(config, "utf8"));
          settings.includes.push("optional-not-created");
          writeFileSync(config, JSON.stringify(settings));
        }
        for (const [file, language] of [["triangle.frag", "glsl"], ["triangle.ps.hlsl", "hlsl"]]) {
          const source = path.join(project, "shaders", file);
          openDocument(source, `shader-tools-${language}`, 1, readFileSync(source, "utf8"));
          openedDocuments.push(source);
        }
      }
      for (const source of openedDocuments) {
        const project = path.dirname(path.dirname(source));
        const context = await connection.request("shaderTools/compilationContext", { textDocument: { uri: uriFor(source) } });
        assert.equal(context.configurationPath.toLowerCase(), path.join(project, ".shadertools.json").toLowerCase());
        const expectedPaths = [project, path.join(project, "include")];
        if (project === projects[0]) expectedPaths.push(path.join(project, "optional-not-created"));
        assert.deepEqual(context.includes.map(value => value.toLowerCase()), expectedPaths.map(value => value.toLowerCase()));
        assert.equal(context.defines.EXAMPLE_TINT, "1");
        const diagnostics = await pullDiagnostics(source);
        assert.ok(!diagnostics.some(item => item.severity === 1), JSON.stringify(diagnostics));
        const hlsl = source.endsWith("hlsl");
        const definition = await connection.request("textDocument/definition", {
          textDocument: { uri: uriFor(source) },
          position: positionAt(readFileSync(source, "utf8"), hlsl ? "ExampleTint(color)" : "exampleTint(vertexColor)", 3),
        });
        const header = uriFor(path.join(project, "include", hlsl ? "colors.hlsli" : "colors.glsl"));
        assert.ok(locationUris(definition).some(uri => decodeURIComponent(uri).toLowerCase() === decodeURIComponent(header).toLowerCase()), JSON.stringify(definition));
        const report = await connection.request("shaderTools/preprocessor", { textDocument: { uri: uriFor(source) }, context });
        assert.equal(report.configuration, context.configurationPath);
        assert.ok(report.includes.some(edge => edge.status === "resolved"));
      }

      const source = openedDocuments[0];
      const childConfig = path.join(projects[0], ".shadertools.json");
      const contextFor = () => connection.request("shaderTools/compilationContext", { textDocument: { uri: uriFor(source) } });
      const before = await contextFor();
      const childSettings = JSON.parse(readFileSync(childConfig, "utf8"));
      childSettings.includes = ["../project B/include"];
      writeFileSync(childConfig, JSON.stringify(childSettings));
      connection.notify("workspace/didChangeConfiguration", { settings: {} });
      const after = await contextFor();
      assert.equal(after.documentVersion, 1);
      assert.deepEqual(after.includes.map(value => value.toLowerCase()), [projects[0].toLowerCase(), path.join(projects[1], "include").toLowerCase()]);
      await assert.rejects(connection.request("shaderTools/preprocessor", { textDocument: { uri: uriFor(source) }, context: before }), /changed/);

      writeFileSync(childConfig, "{ broken");
      connection.notify("workspace/didChangeConfiguration", { settings: {} });
      assert.deepEqual((await contextFor()).includes, after.includes, "partial JSON save lost last valid document settings");
      rmSync(childConfig);
      connection.notify("workspace/didChangeConfiguration", { settings: {} });
      const inherited = await contextFor();
      assert.equal(inherited.configurationPath.toLowerCase(), configurationPath.toLowerCase());
      assert.deepEqual(inherited.includes.map(value => value.toLowerCase()), [temporaryRoot.toLowerCase(), includeDirectory.toLowerCase()]);
    } finally {
      for (const source of openedDocuments) closeDocument(source);
    }
  });

  await runTest("two projects analyze one unsaved header with independent defines and a selected variant", async () => {
    if (options.serverOnly) throw new SkipTest("project context routing is provided by the Bridge");
    const directory = path.join(temporaryRoot, "shared-header projects");
    const common = path.join(directory, "shared");
    mkdirSync(common, { recursive: true });
    const header = path.join(common, "lighting.glsl");
    const diskHeader = "#if PROJECT == 1\nfloat projectA() { return 1.0; }\n#else\nfloat projectB() { return 2.0; }\n#endif\n";
    writeFileSync(header, diskHeader);
    const roots = ["A", "B"].map((name, index) => {
      const project = path.join(directory, name);
      mkdirSync(project);
      const config = path.join(project, ".shadertools.json");
      writeFileSync(config, JSON.stringify({ includes: ["../shared"], defines: { PROJECT: String(index + 1) } }));
      const source = path.join(project, "main.frag");
      const text = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#include "lighting.glsl"\nvoid main() { /* 💡 */ float value = project' + name + '(); }\n';
      writeFileSync(source, text);
      return { source, config, text, name };
    });
    const contextFor = file => connection.request("shaderTools/compilationContext", { textDocument: { uri: uriFor(file) } });
    const definitionFor = (file, text, name) => connection.request("textDocument/definition", {
      textDocument: { uri: uriFor(file) }, position: positionAt(text, name, 2),
    });
    for (const root of roots) openDocument(root.source, "shader-tools-glsl", 1, root.text);
    openDocument(header, "shader-tools-glsl", 1, diskHeader);
    try {
      activeVariant = { url: uriFor(roots[0].source), shadingLanguage: "Glsl", entryPoint: "main", stage: "fragment",
        defines: { PROJECT: "99", SELECTED_A: "1" }, includes: [] };
      connection.notify("textDocument/didChangeShaderVariant", { shaderVariant: activeVariant });
      const before = await contextFor(roots[0].source);
      assert.equal(before.defines.PROJECT, "1");
      assert.equal(before.defines.SELECTED_A, "1");
      assert.equal((await contextFor(header)).configurationPath.toLowerCase(), roots[0].config.toLowerCase());
      const b = await contextFor(roots[1].source);
      assert.equal(b.defines.PROJECT, "2");
      assert.equal(b.defines.SELECTED_A, undefined);

      // An edit in the open shared header must reach both compiler sessions, even when a
      // different session owns navigation for the header tab.
      const headerEdit = "/* 🧭 ж */ float editorOnly() { return 3.0; }\n";
      const end = positionAt(diskHeader, "#endif", "#endif\n".length);
      connection.notify("textDocument/didChange", { textDocument: { uri: uriFor(header), version: 2 },
        contentChanges: [{ range: { start: end, end }, text: headerEdit }] });
      for (const root of roots) {
        const edit = positionAt(root.text, "(); }", 2);
        const addition = " + editorOnly()";
        connection.notify("textDocument/didChange", { textDocument: { uri: uriFor(root.source), version: 2 },
          contentChanges: [{ range: { start: edit, end: edit }, text: addition }] });
        root.text = root.text.replace("(); }", "()" + addition + "; }");
      }
      for (const root of roots) {
        const context = await contextFor(root.source);
        assert.equal(context.documentVersion, 2);
        assert.ok(context.dependencyTree.includes.some(edge => fileURLToPath(edge.url).toLowerCase() === header.toLowerCase()));
        const diagnostics = await pullDiagnostics(root.source);
        assert.ok(!diagnostics.some(item => item.severity === 1), root.name + ": " + JSON.stringify(diagnostics));
        for (const name of ["project" + root.name, "editorOnly"]) {
          const definition = await definitionFor(root.source, root.text, name);
          assert.ok(locationUris(definition).some(target => fileURLToPath(target).toLowerCase() === header.toLowerCase()),
            root.name + " definition of " + name + ": " + JSON.stringify(definition));
          const locations = Array.isArray(definition) ? definition : [definition];
          const location = locations.find(item => fileURLToPath(item.targetUri ?? item.uri).toLowerCase() === header.toLowerCase());
          const range = location.targetSelectionRange ?? location.range;
          assert.deepEqual(range.start, positionAt(diskHeader + headerEdit, name),
            root.name + " UTF-16 target selection for " + name);
        }
      }
      assert.equal((await contextFor(header)).documentVersion, 2);
      assert.equal(readFileSync(header, "utf8"), diskHeader);
    } finally {
      connection.notify("textDocument/didChangeShaderVariant", { shaderVariant: null });
      activeVariant = null;
      closeDocument(header);
      for (const root of roots) closeDocument(root.source);
    }
  });

  await runTest("a newly opened project sees existing unsaved transitive includes", async () => {
    if (options.serverOnly) throw new SkipTest("project context routing is provided by the Bridge");
    const project = path.join(temporaryRoot, "opened last");
    const shared = path.join(temporaryRoot, "edited first");
    mkdirSync(project);
    mkdirSync(shared);
    writeFileSync(path.join(project, ".shadertools.json"), JSON.stringify({ includes: [shared] }));
    writeFileSync(path.join(shared, ".shadertools.json"), JSON.stringify({ validate: false }));
    const leaf = path.join(shared, "leaf.glsl");
    const header = path.join(shared, "first.glsl");
    const source = path.join(project, "main.frag");
    const opened = [];
    try {
      for (const [file, disk, buffer] of [
        [leaf, "float diskLeaf() { return 1.0; }\n", "float editedLeaf() { return 1.0; }\n"],
        [header, "// no includes on disk\n", '#include "leaf.glsl"\n'],
      ]) {
        writeFileSync(file, disk);
        openDocument(file, "shader-tools-glsl", 1, disk);
        opened.push(file);
        connection.notify("textDocument/didChange", { textDocument: { uri: uriFor(file), version: 2 }, contentChanges: [{ text: buffer }] });
      }
      const text = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#include "first.glsl"\nvoid main() { float value = editedLeaf(); }\n';
      writeFileSync(source, text);
      openDocument(source, "shader-tools-glsl", 1, text);
      opened.push(source);
      const diagnostics = await pullDiagnostics(source);
      assert.ok(!diagnostics.some(item => item.severity === 1), JSON.stringify(diagnostics));
      const definition = await connection.request("textDocument/definition", {
        textDocument: { uri: uriFor(source) }, position: positionAt(text, "editedLeaf", 2),
      });
      assert.ok(locationUris(definition).some(target => fileURLToPath(target).toLowerCase() === leaf.toLowerCase()), JSON.stringify(definition));
    } finally {
      for (const file of opened.reverse()) closeDocument(file);
    }
  });

  await runTest("closed selected variant roots retain and reload nested configuration", async () => {
    if (options.serverOnly) throw new SkipTest("configuration discovery is provided by the Bridge");
    const directory = path.join(temporaryRoot, "nested variant");
    const includes = path.join(directory, "include");
    mkdirSync(includes, { recursive: true });
    const config = path.join(directory, ".shadertools.json");
    const source = path.join(directory, "main.frag");
    const header = path.join(includes, "variant-shared.glsl");
    const headerConfig = path.join(includes, ".shadertools.json");
    writeFileSync(headerConfig, JSON.stringify({ defines: { ROOT_TINT: "0.25", HEADER_ONLY: "1" }, validate: false }));
    writeFileSync(header, "vec4 variantColor() { return vec4(ROOT_TINT); }\n");
    const text = '#version 450\n#extension GL_GOOGLE_include_directive : require\n#if USE_HEADER\n#include "variant-shared.glsl"\n#endif\nlayout(location = 0) out vec4 color;\nvoid main() {\n#if USE_HEADER\n color = variantColor();\n#else\n color = vec4(ROOT_TINT);\n#endif\n}\n';
    writeFileSync(source, text);
    const settings = { includes: ["include"], defines: { ROOT_TINT: "1.0", USE_HEADER: "1" } };
    writeFileSync(config, JSON.stringify(settings));
    const contextFor = () => connection.request("shaderTools/compilationContext", { textDocument: { uri: uriFor(source) } });
    let sourceOpen = false;
    let headerOpen = false;
    try {
      activeVariant = { url: uriFor(source), shadingLanguage: "Glsl", entryPoint: "main", stage: "fragment", defines: { SELECTED: "1" }, includes: [] };
      connection.notify("textDocument/didChangeShaderVariant", { shaderVariant: activeVariant });
      assert.equal((await contextFor()).configurationPath.toLowerCase(), config.toLowerCase(), "unopened variant root missed nested config");
      openDocument(source, "shader-tools-glsl", 1, text);
      sourceOpen = true;
      openDocument(header, "shader-tools-glsl", 1, readFileSync(header, "utf8"));
      headerOpen = true;
      closeDocument(source);
      sourceOpen = false;
      const closed = await contextFor();
      assert.equal(closed.configurationPath.toLowerCase(), config.toLowerCase());
      assert.equal(closed.defines.ROOT_TINT, "1.0");
      settings.defines.ROOT_TINT = "0.5";
      writeFileSync(config, JSON.stringify(settings));
      connection.notify("workspace/didChangeConfiguration", { settings: {} });
      const reloaded = await contextFor();
      assert.equal(reloaded.configurationPath.toLowerCase(), config.toLowerCase());
      assert.equal(reloaded.defines.ROOT_TINT, "0.5");
      assert.equal(reloaded.defines.SELECTED, "1");
      assert.ok(!((await pullDiagnostics(header)).some(item => item.severity === 1)), "open header lost variant root settings");
      settings.defines.USE_HEADER = "0";
      writeFileSync(config, JSON.stringify(settings));
      connection.notify("workspace/didChangeConfiguration", { settings: {} });
      assert.equal((await contextFor()).defines.USE_HEADER, "0");
      const headerContext = () => connection.request("shaderTools/compilationContext", { textDocument: { uri: uriFor(header) } });
      const detached = await headerContext();
      assert.equal(detached.configurationPath.toLowerCase(), headerConfig.toLowerCase(), "detached header retained variant root config");
      assert.equal(detached.defines.ROOT_TINT, "0.25");
      assert.equal(detached.defines.HEADER_ONLY, "1");
      connection.notify("textDocument/didChangeShaderVariant", { shaderVariant: null });
      activeVariant = null;
      assert.equal((await headerContext()).configurationPath.toLowerCase(), headerConfig.toLowerCase());
    } finally {
      connection.notify("textDocument/didChangeShaderVariant", { shaderVariant: null });
      activeVariant = null;
      if (sourceOpen) closeDocument(source);
      if (headerOpen) closeDocument(header);
    }
  });

  await runTest("folding ranges cover nested GLSL scopes", async () => {
    const ranges = await connection.request("textDocument/foldingRange", {
      textDocument: { uri: uriFor(featurePath) },
    });
    assert.ok(Array.isArray(ranges) && ranges.length >= 3, `expected at least 3 folding ranges, received ${ranges?.length ?? 0}`);
    assert.ok(ranges.some((range) => range.startLine < range.endLine), "folding ranges did not span multiple lines");
  });

  await runTest("formatting uses clang-format when available", async () => {
    if (!clangFormatDirectory) {
      throw new SkipTest("clang-format was not found in PATH or a known Visual Studio LLVM directory");
    }
    assert.equal(initializeCapabilities.documentFormattingProvider, true, "server did not advertise formatting with clang-format available");
    const formattingPath = path.join(shaderDirectory, "format me.frag.glsl");
    const formattingText = "#version 450\nvoid main(){vec4 value=vec4(1.0);}\n";
    writeFileSync(formattingPath, formattingText);
    openDocument(formattingPath, "shader-tools-glsl", 1, formattingText);
    const edits = await connection.request("textDocument/formatting", {
      textDocument: { uri: uriFor(formattingPath) },
      options: { tabSize: 4, insertSpaces: true },
    });
    assert.ok(Array.isArray(edits) && edits.length > 0, "formatting returned no edits");
    assert.notEqual(edits[0].newText, formattingText, "formatting did not change deliberately unformatted text");
    assert.match(edits[0].newText, /void main\(\) \{/);
    closeDocument(formattingPath);
  });

  await runTest("shader variant switches inactive-region diagnostics", async () => {
    const variantPath = path.join(shaderDirectory, "variant switch.frag.glsl");
    const variantText = `#version 450
#ifdef FEATURE_ON
const float activeValue = 1.0;
#else
const float fallbackValue = 0.0;
#endif
layout(location = 0) out vec4 outColor;
void main() {
    outColor = vec4(fallbackValue);
}
`;
    writeFileSync(variantPath, variantText);
    openDocument(variantPath, "shader-tools-glsl", 1, variantText);
    const before = await pullDiagnostics(variantPath);
    const inactiveBefore = before.filter((item) => item.tags?.includes(1));
    assert.ok(inactiveBefore.length > 0, "default variant produced no inactive-region diagnostic");
    assert.ok(inactiveBefore.some((item) => item.range.start.line <= 2 && item.range.end.line >= 2), "FEATURE_ON branch was not inactive by default");

    activeVariant = {
      url: uriFor(variantPath),
      shadingLanguage: "Glsl",
      entryPoint: "main",
      stage: "fragment",
      defines: { FEATURE_ON: "1" },
      includes: [],
    };
    connection.notify("textDocument/didChangeShaderVariant", { shaderVariant: activeVariant });
    const after = await pullDiagnostics(variantPath);
    const inactiveAfter = after.filter((item) => item.tags?.includes(1));
    assert.ok(inactiveAfter.length > 0, "selected variant produced no inactive-region diagnostic");
    assert.ok(inactiveAfter.some((item) => item.range.start.line <= 4 && item.range.end.line >= 4), "fallback branch did not become inactive");
    assert.ok(!inactiveAfter.some((item) => item.range.start.line <= 2 && item.range.end.line >= 2), "FEATURE_ON branch remained inactive after selecting its variant");
    connection.notify("textDocument/didChangeShaderVariant", { shaderVariant: null });
    activeVariant = null;
    closeDocument(variantPath);
  });

  await runTest("HLSL diagnostics and completion use the bundled DXC", async () => {
    const uri = uriFor(hlslPath);
    let marker = connection.marker();
    openDocument(hlslPath, "shader-tools-hlsl", 1, hlslInvalidText);
    const invalid = await connection.waitForNotification(
      "textDocument/publishDiagnostics",
      (params) => params.uri === uri && params.diagnostics.some((item) => item.severity === 1),
      marker,
      DIAGNOSTIC_TIMEOUT_MS,
    );
    assert.ok(invalid.diagnostics.some((item) => /expected|error|syntax|;/i.test(item.message)), "DXC diagnostic did not describe the invalid shader");

    marker = connection.marker();
    connection.notify("textDocument/didChange", {
      textDocument: { uri, version: 2 },
      contentChanges: [{ text: hlslValidText }],
    });
    await connection.waitForNotification(
      "textDocument/publishDiagnostics",
      (params) => params.uri === uri && !params.diagnostics.some((item) => item.severity === 1),
      marker,
      DIAGNOSTIC_TIMEOUT_MS,
    );

    const completion = await connection.request("textDocument/completion", {
      textDocument: { uri },
      position: positionAt(hlslValidText, "return localShade", "return ".length),
      context: { triggerKind: 1 },
    });
    const labels = new Set(completionItems(completion).map((item) => item.label));
    assert.ok(labels.has("localShade"), `localShade missing from ${labels.size} HLSL completion items`);
    assert.ok(labels.has("saturate"), `saturate intrinsic missing from ${labels.size} HLSL completion items`);
    closeDocument(hlslPath);
  });

  await runTest("manual validation returns current GLSL and HLSL diagnostics", async () => {
    const glsl = await connection.request("textDocument/diagnostic", { textDocument: { uri: uriFor(featurePath) } });
    assert.equal(glsl?.kind, "full");
    assert.ok(Array.isArray(glsl.items));
    assert.equal(glsl.items.some(item => item.severity === 1), false);
    openDocument(hlslPath, "shader-tools-hlsl", 3, hlslValidText);
    const hlsl = await connection.request("textDocument/diagnostic", { textDocument: { uri: uriFor(hlslPath) } });
    assert.equal(hlsl?.kind, "full");
    assert.ok(Array.isArray(hlsl.items));
    assert.equal(hlsl.items.some(item => item.severity === 1), false);
    const definition = await connection.request("textDocument/definition", {
      textDocument: { uri: uriFor(hlslPath) },
      position: positionAt(hlslValidText, "shared ü.hlsli", 8),
    });
    assert.ok(locationUris(definition).some((target) =>
      decodeURIComponent(target).toLowerCase() === decodeURIComponent(uriFor(sharedHlslPath)).toLowerCase()),
      "the HLSL include filename did not resolve to its configured header");
    closeDocument(hlslPath);
  });

  await runTest("shutdown leaves no bridge-owned server process", async () => {
    closeDocument(featurePath);
    const result = await connection.request("shutdown", null);
    assert.equal(result, null);
    shutdownSent = true;
    connection.notify("exit", null);
    connection.child.stdin.end();
    let exit;
    try {
      exit = await connection.waitForExit();
    } catch (error) {
      if (options.serverOnly) {
        connection.terminate();
        await connection.waitForExit();
        console.log("INFO direct server required termination after protocol shutdown");
        return;
      }
      throw error;
    }
    assert.equal(exit.code, 0, `host exited with code ${exit.code}, signal ${exit.signal}`);
    if (!options.serverOnly) {
      assert.ok(connection.serverPids.size > 0, `bridge did not report a child PID on stderr:\n${connection.stderr}`);
      for (const pid of connection.serverPids) {
        assert.equal(isProcessAlive(pid), false, `shader-language-server PID ${pid} survived bridge shutdown`);
      }
    }
  });

  await runTest("restart accepts a VS UTF-8 preamble and a workspace with a trailing separator", async () => {
    if (options.serverOnly) throw new SkipTest("this tests VS-to-Bridge startup");
    const workspace = temporaryRoot + path.sep;
    const restarted = new LspConnection(options.bridge, ["--server", options.server, "--workspace", workspace], {
      cwd: temporaryRoot, env: environment,
    });
    try {
      // Visual Studio's StreamWriter can send its UTF-8 preamble before the first LSP header.
      restarted.child.stdin.write(Buffer.from([0xef, 0xbb, 0xbf]));
      const rootUri = uriFor(workspace);
      const result = await restarted.request("initialize", {
        processId: process.pid, rootPath: workspace, rootUri,
        workspaceFolders: [{ uri: rootUri, name: "Reopened shader workspace" }], capabilities: {},
      });
      assert.ok(result.capabilities.documentSymbolProvider);
      restarted.notify("initialized", {});
      restarted.notify("textDocument/didOpen", {
        textDocument: { uri: uriFor(featurePath), languageId: "shader-tools-glsl", version: 1, text: featureText },
      });
      const symbols = await restarted.request("textDocument/documentSymbol", { textDocument: { uri: uriFor(featurePath) } });
      assert.ok(symbols.some(item => item.name === "computeLocal"));
      assert.equal(await restarted.request("shutdown", null), null);
      restarted.notify("exit", null);
      restarted.child.stdin.end();
      assert.equal((await restarted.waitForExit()).code, 0);
      assert.ok(restarted.serverPids.size > 0);
      for (const pid of restarted.serverPids) assert.equal(isProcessAlive(pid), false);
    } finally {
      if (!restarted.exitResult) {
        restarted.terminate();
        await restarted.waitForExit();
      }
    }
  });

  await runTest("forced bridge termination kills project servers and active clang-format descendant", async () => {
    if (options.serverOnly) throw new SkipTest("job containment belongs to the bridge");
    if (process.platform !== "win32") throw new SkipTest("Windows Job Objects are only available on Windows");

    const formatterDirectory = path.join(temporaryRoot, "blocking formatter");
    const formatterExecutable = await compileBlockingClangFormat(formatterDirectory);
    const formatterMarker = path.join(temporaryRoot, "active formatter.pid");
    assert.equal(path.basename(formatterExecutable).toLowerCase(), "clang-format.exe");

    const environment = {
      ...process.env,
      SHADER_TOOLS_FORMATTER_MARKER: formatterMarker,
    };
    const forced = new LspConnection(
      options.bridge,
      [
        "--server",
        options.server,
        "--workspace",
        temporaryRoot,
        "--format-directory",
        formatterDirectory,
      ],
      { cwd: temporaryRoot, env: environment },
    );

    try {
      const rootUri = uriFor(temporaryRoot);
      const initialize = await forced.request("initialize", {
        processId: process.pid,
        clientInfo: { name: "shader-tools-lifecycle-smoke", version: "1" },
        rootPath: temporaryRoot,
        rootUri,
        workspaceFolders: [{ uri: rootUri, name: path.basename(temporaryRoot) }],
        capabilities: {
          general: { positionEncodings: ["utf-16"] },
          workspace: { configuration: true, workspaceFolders: true },
        },
      });
      assert.equal(initialize?.capabilities?.documentFormattingProvider, true, "blocking clang-format was not detected");
      forced.notify("initialized", {});

      const formattingPath = path.join(shaderDirectory, "force kill formatting.frag.glsl");
      const formattingText = "#version 450\nvoid main(){vec4 value=vec4(1.0);}\n";
      writeFileSync(formattingPath, formattingText);
      forced.notify("textDocument/didOpen", {
        textDocument: {
          uri: uriFor(formattingPath),
          languageId: "shader-tools-glsl",
          version: 1,
          text: formattingText,
        },
      });
      const secondProject = path.join(temporaryRoot, "force kill second project");
      mkdirSync(secondProject);
      writeFileSync(path.join(secondProject, ".shadertools.json"), JSON.stringify({ defines: { SECOND_PROJECT: "1" } }));
      const secondFile = path.join(secondProject, "other.frag");
      writeFileSync(secondFile, formattingText);
      const secondUri = uriFor(secondFile);
      forced.notify("textDocument/didOpen", { textDocument: { uri: secondUri, languageId: "shader-tools-glsl", version: 1, text: formattingText } });
      const symbols = await forced.request("textDocument/documentSymbol", { textDocument: { uri: secondUri } });
      assert.ok(symbols.some(symbol => symbol.name === "main"));
      const formattingRequest = forced.request("textDocument/formatting", {
        textDocument: { uri: uriFor(formattingPath) },
        options: { tabSize: 4, insertSpaces: true },
      }).catch(() => undefined);

      const formatterPid = await waitForPidMarker(formatterMarker, DIAGNOSTIC_TIMEOUT_MS);
      assert.ok(forced.serverPids.size >= 2, `expected PIDs for both project servers:\n${forced.stderr}`);
      assert.ok(!forced.serverPids.has(formatterPid), "formatter PID unexpectedly equals a server PID");
      for (const pid of forced.serverPids) assert.equal(isProcessAlive(pid), true, `project PID ${pid} already exited`);
      assert.equal(isProcessAlive(formatterPid), true, "blocking formatter exited before forced bridge termination");

      const serverExits = [...forced.serverPids].map(pid => waitForPidExit(pid, 5_000));
      const formatterExit = waitForPidExit(formatterPid, 5_000);
      assert.equal(forced.child.kill(), true, `could not terminate owned bridge PID ${forced.child.pid}`);
      await forced.waitForExit(5_000);
      await Promise.all([...serverExits, formatterExit, formattingRequest]);
      for (const pid of forced.serverPids) assert.equal(isProcessAlive(pid), false, `server PID ${pid} survived forced bridge termination`);
      assert.equal(isProcessAlive(formatterPid), false, `clang-format PID ${formatterPid} survived forced bridge termination`);
    } finally {
      if (!forced.exitResult) {
        forced.terminate();
        try {
          await forced.waitForExit(3_000);
        } catch {
          // The bridge was force-terminated; its job owns descendant cleanup.
        }
      }
    }
  });
}

try {
  await main();
} catch (error) {
  failed += 1;
  failures.push({ name: "harness", error });
  console.error(`FAIL harness: ${error.stack ?? error}`);
} finally {
  if (connection && !connection.exitResult) {
    try {
      if (initialized && !shutdownSent) {
        await connection.request("shutdown", null, 2_000);
        connection.notify("exit", null);
        connection.child.stdin.end();
        await connection.waitForExit(3_000);
      }
    } catch {
      connection.terminate();
      try {
        await connection.waitForExit(2_000);
      } catch {
        // The process was already force-terminated; there is nothing else owned by this harness.
      }
    }
  }

  const resolvedTemporaryRoot = path.resolve(temporaryRoot);
  const resolvedTempDirectory = path.resolve(os.tmpdir());
  if (
    resolvedTemporaryRoot.startsWith(`${resolvedTempDirectory}${path.sep}`) &&
    path.basename(resolvedTemporaryRoot).startsWith("Shader Tools Ω workspace ")
  ) {
    rmSync(resolvedTemporaryRoot, { recursive: true, force: true });
  } else {
    failures.push({ name: "cleanup", error: new Error(`Refused to remove unexpected path ${resolvedTemporaryRoot}`) });
    failed += 1;
  }
}

console.log(`TOTAL ${passed + failed + skipped} PASS ${passed} FAIL ${failed} SKIP ${skipped}`);
if (failures.length > 0) {
  for (const failure of failures) console.error(`  ${failure.name}: ${failure.error.stack ?? failure.error}`);
  process.exitCode = 1;
}
