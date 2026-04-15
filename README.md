# AgentSmithers dnSpyEx MCPServer Extension
This is a dnSpy MCPServer Extension for the use of automated AI .NET reverse engineering
The Example1.Extension has been updated to include the MCPServer logic with a few starting function to feed the decompiled code back to the LLM.
This is an ongoing project and I plan to continue to add commands as time persist.

## Build and install

The extension now builds for both:

- `net48` for classic dnSpy / .NET Framework builds
- `net8.0-windows` for modern `dnSpy-net-win32` and `dnSpy-net-win64`

Build the whole solution:

```powershell
.\build-ewdk.cmd
```

The batch script now always does this automatically:

1. Clean old `Release` outputs from the whole solution
2. Delete old staged MCP packages under `mcp-build\`
3. Build the full solution in `Release`
4. Stage the extension packages into:

- `mcp-build\AgentSmithersMCPServer\Release\net48`
- `mcp-build\AgentSmithersMCPServer\Release\net8.0-windows`

Stage extension packages and install them into detected dnSpy folders:

```powershell
pwsh .\deploy-extension.ps1 -Configuration Release
```

Build, stage, and install in one command:

```powershell
pwsh .\deploy-extension.ps1 -Build -Configuration Release
```

Latest verification: the repository was rebuilt after the debugger MCP expansion and `Example1.Extension` emitted successfully for both `net48` and `net8.0-windows`. The remaining open risk is runtime smoke-testing inside a live dnSpy debug session, not compile-time build health.

Install to explicit dnSpy paths:

```powershell
pwsh .\deploy-extension.ps1 -Configuration Release -DnSpyPath `
  'C:\Users\emet\Downloads\Compressed\dnSpy-net-win32',`
  'C:\Users\emet\Downloads\Compressed\dnSpy-net-win64'
```

Package outputs are staged here:

- `mcp-build\AgentSmithersMCPServer\Release\net48`
- `mcp-build\AgentSmithersMCPServer\Release\net8.0-windows`

Direct build outputs remain here:

- `Extensions\Examples\Example1.Extension\bin\Release\net48`
- `Extensions\Examples\Example1.Extension\bin\Release\net8.0-windows`

The installer copies the extension into the actual dnSpy binary base:

- `net48`: `<dnSpy root>\Extensions\AgentSmithersMCPServer`
- `net8.0-windows`: `<dnSpy root>\bin\Extensions\AgentSmithersMCPServer`

Quick usage:

1. Run `.\build-ewdk.cmd`
2. Pick the correct staged package:
   `net48` for classic dnSpy / .NET Framework builds
   `net8.0-windows` for `dnSpy-net-win32` and `dnSpy-net-win64`
3. Copy everything from that staged folder into:
   `net48`: `<dnSpy root>\Extensions\AgentSmithersMCPServer`
   `net8.0-windows`: `<dnSpy root>\bin\Extensions\AgentSmithersMCPServer`
4. Start dnSpy, click `Yes` when the MCP prompt appears, then connect your MCP client to `127.0.0.1:3003`

For example:

- `C:\Users\emet\Downloads\Compressed\dnSpy-net-win32\bin\Extensions\AgentSmithersMCPServer`
- `C:\Users\emet\Downloads\Compressed\dnSpy-net-win64\bin\Extensions\AgentSmithersMCPServer`

To execute the server, run with enough privileges for the configured host/port binding. The default listener is `127.0.0.1:3003`, not `+:3003`.

The following command will suffice:

cd {dnspy directory}

dnspy.exe --extension-directory {Path to extension}

Ex. dnspy.exe --extension-directory "C:\repos\dnSpy\Extensions\Examples\Example1.Extension\bin\Debug\net48"

Example 2 in CMD: C:\Users\user>"C:\Users\user\source\repos\DnSpy-MCPserver-Extension\dnSpy\dnSpy\bin\Debug\net48\dnspy.exe" --extension-directory "C:\Users\user\source\repos\DnSpy-MCPserver-Extension\dnSpy\dnSpy\bin\Debug\net48\Extensions"

In alternative you may copy the release into the correct Extension folder for that dnSpy flavor. For `dnSpy-net-win32` and `dnSpy-net-win64`, that means `dnSpy\bin\Extensions\AgentSmithersMCPServer`.

# Modifying the project
Ensure to compile the primary DnSpy Soultion (Clean and rebuild) so that the dependancies for "AgentSmithers DnSpyEx MCPServer" can use them for its own build. (Note: When compiling DnSpyEx you may have approx. ~20 errors, this will not prevent the 45 Projects from compiling)
<img width="846" alt="image" src="https://github.com/user-attachments/assets/4d9269fa-ab0e-4392-8042-f79b31795e43" />
<img width="991" alt="image" src="https://github.com/user-attachments/assets/57e708b3-05e9-4edd-8b72-53c0850c5304" />

Once dnSpy is fully cleaned and compiled, navigate to the Example1 Extension Project. In there is the modified code.
Make your adjustments, compile then use the --extension-directory argument when executing DnSpy.exe to point to your projects extension path.

In alternative you may pull down the latest dnSpyEx then rename the Extension sample folder to another name and bring the modified extension folder over to recompile.

# Supported commands
The extension now exposes assembly inspection/edit commands plus debugger-oriented MCP commands.

Core inspection and edit commands:

```text
Help
Get_Selected_Node
Get_Server_Status
Get_Loaded_Assemblies
Get_Assembly_References
Namespaces_From_Assembly
Get_Global_Namespaces
Classes_From_Namespace
Search_Types
Search_Methods
Get_Type_Details
Search_String_Literals
Get_Class_Sourcecode
Get_Method_Prototypes
Get_Method_SourceCode
Update_Method_SourceCode
Get_Function_Opcodes
Set_Function_Opcodes
Overwrite_Full_Func_Opcodes
Update_Tabs_View
Rename_Namespace
Rename_Class
Rename_Method
Dump.All
```

Debugger MCP commands:

```text
Dbg_Get_Status
Dbg_List_Attachable_Processes
Dbg_Attach_Process
Dbg_List_Processes
Dbg_Select_Process
Dbg_List_Threads
Dbg_Select_Thread
Dbg_List_Modules
Dbg_Get_CallStack
Dbg_Select_Frame
Dbg_Get_Locals
Dbg_Get_Autos
Dbg_Get_Return_Values
Dbg_Evaluate_Expression
Dbg_Get_Value_Children
Dbg_List_Breakpoints
Dbg_Add_Breakpoint
Dbg_Add_Current_Frame_Breakpoint
Dbg_Remove_Breakpoint
Dbg_Break_All
Dbg_Continue
Dbg_Step_Into
Dbg_Step_Over
Dbg_Step_Out
Dbg_Get_Current_Exception
Dbg_Read_Memory
Dbg_Get_Session_Snapshot
Dbg_Detach_All
```

Debugger notes:

- The `Dbg_*` commands require a dnSpy build with debugger services available.
- `Dbg_Attach_Process` attaches by PID to a managed process dnSpy can see.
- `Dbg_Get_Locals`, `Dbg_Get_Autos`, `Dbg_Get_Return_Values`, `Dbg_Evaluate_Expression`, and `Dbg_Get_Value_Children` all run against the currently selected dnSpy frame, so `Dbg_Select_Frame` can be used to pivot context first.
- `Dbg_Add_Breakpoint` currently targets managed .NET methods by assembly, namespace, class, method, optional `MethodSignature`, and IL offset.
- `Dbg_Get_Session_Snapshot` is the best one-shot tool when the MCP client needs broad debug context with minimal round-trips.
- `Dbg_Evaluate_Expression` and `Dbg_Get_Value_Children` stay aligned with dnSpy's native debugger language/value-node pipeline instead of using a custom evaluator.
- `Dbg_Read_Memory` reads raw bytes from the selected debugged process and returns a hex dump.
- Step and call-stack commands only make sense while the debugger is attached and paused on a valid thread/frame.

Debug eval workflow:

1. Attach or start debugging with dnSpy.
2. Pause the target with `Dbg_Break_All` if it is still running.
3. Inspect and select context with `Dbg_List_Processes`, `Dbg_List_Threads`, `Dbg_Get_CallStack`, `Dbg_Select_Process`, `Dbg_Select_Thread`, and `Dbg_Select_Frame`.
4. Use `Dbg_Get_Locals`, `Dbg_Get_Autos`, `Dbg_Get_Return_Values`, `Dbg_Evaluate_Expression`, or `Dbg_Get_Value_Children` against that active paused frame.

Evaluation command parameters:

- `NoSideEffects=true` asks dnSpy to avoid function/property execution when possible.
- `MaxChildrenPerNode` limits direct child expansion for each returned value node.
- `MaxChildren` limits direct child expansion for `Dbg_Get_Value_Children`.
- `ShowCompilerGenerated` includes compiler-generated locals/captures.
- `ShowDecompilerGenerated` includes synthetic locals introduced by decompilation.
- `ShowRawLocals` prefers raw metadata locals instead of lifted/captured views.

Examples:

```text
Dbg_Get_Locals(4, false, false, false)
Dbg_Evaluate_Expression("request.Headers", true, 6)
Dbg_Get_Value_Children("this", 16, true)
```

Typical output shape is a compact one-line summary per node, for example:

```text
[0] Kind=Parameter Name=request Value={HttpRequestMessage} Type=System.Net.Http.HttpRequestMessage Expr=request ReadOnly=True SideEffects=False HasChildren=True ChildCount=8
    [0] Name=Headers Value=Count = 5 Type=System.Net.Http.Headers.HttpRequestHeaders Expr=request.Headers ReadOnly=True SideEffects=False HasChildren=True ChildCount=5
```

Caveats:

- These evaluation tools require a paused managed frame and dnSpy evaluation services.
- `NoSideEffects=true` is a best-effort safe mode, not a hard guarantee against every runtime-specific side effect.
- Long-running or stuck debugger evaluations can still fail or hang depending on the target process state.

These commands allow you to inspect and modify .NET assemblies loaded in dnSpyEx and now also drive a live dnSpy debug session through MCP.
# Once the extension is loaded
A messagebox will appear allowing you a chance to attach your debugger if nessessary
![image](https://github.com/user-attachments/assets/f7a53b4c-e273-435e-9098-d92eb54fa84e)

Click `Yes` and the MCP server will start on the configured host and port. The default is `127.0.0.1:3003`.

You can now change host, port, autostart and logging behavior from the dnSpy options page:

- `Options -> dnSpy MCP Server`

Connect via SSE or if your application only supports STDIO (Claude Desktop / Windsurf) grab a copy of the STDIO<->SSE bridge
https://github.com/AgentSmithers/MCPProxy-STDIO-to-SSE

Attach the Bridge AFTER hitting "OK" and your good to go.

![image](https://github.com/user-attachments/assets/b8fa494e-2962-4733-a1f9-83f729c29811)

![image](https://github.com/user-attachments/assets/851e1ef0-e9ee-400b-b185-4cbde3739894)
