# AgentSmithers dnSpy MCP Extension

MCP extension for dnSpy that exposes reverse-engineering and debugger workflows to MCP clients!

## Features

- Runs an MCP server directly inside dnSpy over SSE
- Supports classic `net48` dnSpy and modern `dnSpy-net-win32` / `dnSpy-net-win64`
- Exposes static analysis, metadata inspection, IL editing, and debugger-oriented tools
- Integrates runtime logging into dnSpy via the `MCP Log` output tab

## Requirements

- Windows
- dnSpy or dnSpy-net build with extension support
- EWDK or an equivalent Visual Studio/MSBuild environment capable of building the dnSpy solution

## Build

Build the full solution and stage the extension packages:

```powershell
.\build-ewdk.cmd
```

What the build script does:

- Cleans old solution/package outputs
- Builds the full solution in `Release`
- Copies deployable extension files to `mcp-build\`

Staged outputs:

- `mcp-build\AgentSmithersMCPServer\Release\net48`
- `mcp-build\AgentSmithersMCPServer\Release\net8.0-windows`

## Install

Build the solution:

```powershell
.\build-ewdk.cmd
```

Then copy the correct staged package into the matching dnSpy extension folder:

- Classic dnSpy / `net48`: `<dnSpy root>\Extensions\AgentSmithersMCPServer`
- `dnSpy-net-win32` / `dnSpy-net-win64`: `<dnSpy root>\bin\Extensions\AgentSmithersMCPServer`

Staged outputs:

- `mcp-build\AgentSmithersMCPServer\Release\net48`
- `mcp-build\AgentSmithersMCPServer\Release\net8.0-windows`

## Codex Configuration

Add this to your Codex `config.toml`:

```toml
[mcp_servers.dnspy-mcp]
enabled = true
url = "http://127.0.0.1:3003/sse/"
```

## Tooling Overview

The extension currently exposes a broad MCP toolset across three main areas:

- Server and session tools: listener status, selected node, inventory/help
- Static analysis and editing tools: assemblies, namespaces, types, methods, references, source, IL, renaming
- Debugger tools: attach, process/thread/frame selection, call stack, locals, autos, evaluation, breakpoints, stepping, memory, session snapshot

A few representative tools:

```text
Get_Server_Status
Get_Loaded_Assemblies
Browse_Assembly_Map
Search_Types
List_Type_Members
Get_Method_SourceCode
Update_Method_SourceCode
Get_Function_Opcodes
Set_Function_Opcodes
Dbg_Get_Status
Dbg_Attach_Process
Dbg_Get_CallStack
Dbg_Get_Locals
Dbg_Evaluate_Expression
Dbg_Add_Breakpoint
Dbg_Step_Over
Dbg_Read_Memory
Dbg_Get_Session_Snapshot
```

## Development Notes

- Main extension source lives under `Extensions\Examples\Example1.Extension`
- Build artifacts intended for deployment are staged under `mcp-build\`
- The project is aligned to dnSpy's native services so debugger-related MCP calls stay compatible with the host application instead of relying on a separate runtime
