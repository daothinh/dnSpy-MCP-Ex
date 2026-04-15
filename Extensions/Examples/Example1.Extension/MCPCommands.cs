using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.TreeView;
using ICSharpCode.TreeView;
using static Example1.Extension.SimpleMcpServer;

namespace Example1.Extension
{
    partial class MCPCommands
    {
		[Command("Help", MCPCmdDescription = "Call this command before executing any other dnSpyEx command for assistance on how to pass arguments.")]
		public static string Help() {
			return BuildDynamicHelp();
		}

		static IEnumerable<ModuleDocumentNode> GetModuleNodes(string assemblyName = null) {
			var nodes = Global.MyTreeView.GetAllModuleNodes().ToList();
			if (string.IsNullOrWhiteSpace(assemblyName))
				return nodes;

			return nodes.Where(node => {
				var module = node.GetModule();
				var asmName = module.Assembly?.Name ?? module.Name;
				return string.Equals(asmName, assemblyName, StringComparison.OrdinalIgnoreCase);
			});
		}

		static TypeDef FindType(ModuleDef module, string @namespace, string className) =>
			module.GetTypes().FirstOrDefault(type =>
				string.Equals(type.Namespace ?? string.Empty, @namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(type.Name.String, className, StringComparison.OrdinalIgnoreCase));

		static int NormalizeMaxResults(int maxResults, int defaultValue, int hardLimit = 200) {
			if (maxResults <= 0)
				return defaultValue;
			return Math.Min(maxResults, hardLimit);
		}

		static bool ContainsIgnoreCase(string source, string value) =>
			!string.IsNullOrEmpty(source) &&
			!string.IsNullOrEmpty(value) &&
			source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

		static string SanitizeForSingleLine(string value, int maxLength = 120) {
			if (string.IsNullOrEmpty(value))
				return string.Empty;

			var sanitized = value.Replace("\r", "\\r").Replace("\n", "\\n");
			return sanitized.Length <= maxLength ? sanitized : sanitized.Substring(0, maxLength) + "...";
		}

		[Command("Get_Server_Status", MCPCmdDescription = "Returns the current MCP server host, port, session count and startup settings.")]
		public static string GetServerStatus() {
			var sb = new StringBuilder();
			var server = Global.MySimpleMCPServer;
			var settings = Global.MySettings;

			if (server == null) {
				sb.AppendLine("Running: False");
				sb.AppendLine("Initialized: False");
				if (settings != null) {
					sb.AppendLine($"ConfiguredHost: {settings.ServerHost}");
					sb.AppendLine($"ConfiguredPort: {settings.ServerPort}");
					sb.AppendLine($"AutoStartServer: {settings.AutoStartServer}");
					sb.AppendLine($"ShowStartupPrompt: {settings.ShowStartupPrompt}");
					sb.AppendLine($"VerboseLogging: {settings.VerboseLogging}");
					sb.AppendLine($"OpenAssemblyExplorerOnStartup: {settings.OpenAssemblyExplorerOnStartup}");
				}
				sb.Append("Status: MCP server instance has not been initialized yet.");
				return sb.ToString();
			}

			sb.AppendLine($"Running: {server.IsRunning}");
			sb.AppendLine("Initialized: True");
			sb.AppendLine($"ListenHost: {server.ListenHost}");
			sb.AppendLine($"ListenPort: {server.ListenPort}");
			sb.AppendLine($"SseEndpoint: {server.SseEndpoint}");
			sb.AppendLine($"MessageEndpoint: {server.MessageEndpoint}");
			sb.AppendLine($"ActiveSessions: {server.SessionCount}");
			sb.AppendLine($"RegisteredCommands: {server.CommandCount}");
			sb.AppendLine($"Status: {server.LastStatusMessage}");
			if (!string.IsNullOrWhiteSpace(server.LastStartError))
				sb.AppendLine($"LastStartError: {SanitizeForSingleLine(server.LastStartError, 240)}");
			if (settings != null) {
				sb.AppendLine($"AutoStartServer: {settings.AutoStartServer}");
				sb.AppendLine($"ShowStartupPrompt: {settings.ShowStartupPrompt}");
				sb.AppendLine($"VerboseLogging: {settings.VerboseLogging}");
				sb.AppendLine($"OpenAssemblyExplorerOnStartup: {settings.OpenAssemblyExplorerOnStartup}");
			}
			return sb.ToString();
		}

		[Command("Search_Types", MCPCmdDescription = "Searches types in a loaded assembly by name or full name.")]
		public static string SearchTypes(string Assembly, string SearchTerm, int MaxResults = 50) {
			try {
				if (string.IsNullOrWhiteSpace(SearchTerm))
					return "SearchTerm cannot be empty.";

				var maxResults = NormalizeMaxResults(MaxResults, 50);
				var matches = GetModuleNodes(Assembly)
					.SelectMany(modNode => modNode.GetModule().GetTypes().Select(type => new {
						AssemblyName = modNode.GetModule().Assembly?.Name ?? modNode.GetModule().Name,
						Type = type,
					}))
					.Where(x => ContainsIgnoreCase(x.Type.FullName, SearchTerm) || ContainsIgnoreCase(x.Type.Name.String, SearchTerm))
					.OrderBy(x => x.Type.FullName, StringComparer.OrdinalIgnoreCase)
					.Take(maxResults + 1)
					.ToList();

				if (matches.Count == 0)
					return $"No types found matching '{SearchTerm}' in assembly {Assembly}.";

				var sb = new StringBuilder();
				foreach (var match in matches.Take(maxResults))
					sb.AppendLine($"{match.AssemblyName} :: {match.Type.FullName}");
				if (matches.Count > maxResults)
					sb.AppendLine($"... truncated to {maxResults} results");
				return sb.ToString();
			}
			catch (Exception ex) {
				return $"Exception: {ex.Message}";
			}
		}

		[Command("Search_Methods", MCPCmdDescription = "Searches methods in a loaded assembly by name or signature.")]
		public static string SearchMethods(string Assembly, string SearchTerm, int MaxResults = 100) {
			try {
				if (string.IsNullOrWhiteSpace(SearchTerm))
					return "SearchTerm cannot be empty.";

				var maxResults = NormalizeMaxResults(MaxResults, 100);
				var matches = GetModuleNodes(Assembly)
					.SelectMany(modNode => modNode.GetModule().GetTypes().SelectMany(type => type.Methods.Select(method => new {
						AssemblyName = modNode.GetModule().Assembly?.Name ?? modNode.GetModule().Name,
						TypeName = type.FullName,
						Method = method,
					})))
					.Where(x => ContainsIgnoreCase(x.Method.Name.String, SearchTerm) || ContainsIgnoreCase(x.Method.FullName, SearchTerm))
					.OrderBy(x => x.TypeName, StringComparer.OrdinalIgnoreCase)
					.ThenBy(x => x.Method.Name.String, StringComparer.OrdinalIgnoreCase)
					.Take(maxResults + 1)
					.ToList();

				if (matches.Count == 0)
					return $"No methods found matching '{SearchTerm}' in assembly {Assembly}.";

				var sb = new StringBuilder();
				foreach (var match in matches.Take(maxResults))
					sb.AppendLine($"{match.AssemblyName} :: {match.TypeName} :: {match.Method.FullName}");
				if (matches.Count > maxResults)
					sb.AppendLine($"... truncated to {maxResults} results");
				return sb.ToString();
			}
			catch (Exception ex) {
				return $"Exception: {ex.Message}";
			}
		}

		[Command("Get_Type_Details", MCPCmdDescription = "Returns base type, interfaces, fields, properties, events and methods for a target type.")]
		public static string GetTypeDetails(string Assembly, string Namespace, string ClassName) {
			try {
				foreach (var modNode in GetModuleNodes(Assembly)) {
					var module = modNode.GetModule();
					var type = FindType(module, Namespace, ClassName);
					if (type == null)
						continue;

					var sb = new StringBuilder();
					sb.AppendLine($"Assembly: {module.Assembly?.Name ?? module.Name}");
					sb.AppendLine($"Module: {module.Name}");
					sb.AppendLine($"Type: {type.FullName}");
					sb.AppendLine($"BaseType: {type.BaseType?.FullName ?? "<none>"}");
					sb.AppendLine($"Interfaces: {(type.Interfaces.Count == 0 ? "<none>" : string.Join(", ", type.Interfaces.Select(i => i.Interface.FullName)))}");
					sb.AppendLine($"NestedTypes: {type.NestedTypes.Count}");
					sb.AppendLine();

					sb.AppendLine("Fields:");
					foreach (var field in type.Fields.OrderBy(f => f.Name.String, StringComparer.OrdinalIgnoreCase))
						sb.AppendLine($"  {field.FieldType.FullName} {field.Name}");
					if (type.Fields.Count == 0)
						sb.AppendLine("  <none>");

					sb.AppendLine();
					sb.AppendLine("Properties:");
					foreach (var property in type.Properties.OrderBy(p => p.Name.String, StringComparer.OrdinalIgnoreCase))
						sb.AppendLine($"  {property.PropertySig.GetRetType().FullName} {property.Name}");
					if (type.Properties.Count == 0)
						sb.AppendLine("  <none>");

					sb.AppendLine();
					sb.AppendLine("Events:");
					foreach (var evt in type.Events.OrderBy(e => e.Name.String, StringComparer.OrdinalIgnoreCase))
						sb.AppendLine($"  {evt.EventType.FullName} {evt.Name}");
					if (type.Events.Count == 0)
						sb.AppendLine("  <none>");

					sb.AppendLine();
					sb.AppendLine("Methods:");
					foreach (var method in type.Methods.OrderBy(m => m.Name.String, StringComparer.OrdinalIgnoreCase))
						sb.AppendLine($"  {method.FullName}");

					return sb.ToString();
				}

				return $"Type {Namespace}.{ClassName} not found in assembly {Assembly}.";
			}
			catch (Exception ex) {
				return $"Exception: {ex.Message}";
			}
		}

		[Command("Get_Assembly_References", MCPCmdDescription = "Lists assembly and module references for a target assembly.")]
		public static string GetAssemblyReferences(string Assembly) {
			try {
				var sb = new StringBuilder();
				var any = false;

				foreach (var modNode in GetModuleNodes(Assembly)) {
					var module = modNode.GetModule();
					any = true;
					sb.AppendLine($"Module: {module.Name}");
					sb.AppendLine("AssemblyRefs:");
					var assemblyRefs = module.GetAssemblyRefs().Select(r => r.FullName).Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
					foreach (var assemblyRef in assemblyRefs)
						sb.AppendLine($"  {assemblyRef}");
					if (assemblyRefs.Count == 0)
						sb.AppendLine("  <none>");

					sb.AppendLine("ModuleRefs:");
					var moduleRefs = module.GetModuleRefs().Select(r => r.FullName).Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
					foreach (var moduleRef in moduleRefs)
						sb.AppendLine($"  {moduleRef}");
					if (moduleRefs.Count == 0)
						sb.AppendLine("  <none>");
					sb.AppendLine();
				}

				return any ? sb.ToString() : $"Assembly {Assembly} is not loaded.";
			}
			catch (Exception ex) {
				return $"Exception: {ex.Message}";
			}
		}

		[Command("Search_String_Literals", MCPCmdDescription = "Searches IL string literals in the target assembly.")]
		public static string SearchStringLiterals(string Assembly, string SearchTerm, int MaxResults = 100) {
			try {
				if (string.IsNullOrWhiteSpace(SearchTerm))
					return "SearchTerm cannot be empty.";

				var maxResults = NormalizeMaxResults(MaxResults, 100);
				var matches = new List<string>();

				foreach (var modNode in GetModuleNodes(Assembly)) {
					var module = modNode.GetModule();
					foreach (var type in module.GetTypes()) {
						foreach (var method in type.Methods) {
							if (!method.HasBody)
								continue;

							foreach (var instruction in method.Body.Instructions) {
								if (instruction.OpCode != OpCodes.Ldstr || instruction.Operand is not string literal)
									continue;

								if (!ContainsIgnoreCase(literal, SearchTerm))
									continue;

								matches.Add($"{module.Assembly?.Name ?? module.Name} :: {type.FullName} :: {method.Name} => \"{SanitizeForSingleLine(literal)}\"");
								if (matches.Count > maxResults)
									break;
							}
							if (matches.Count > maxResults)
								break;
						}
						if (matches.Count > maxResults)
							break;
					}
					if (matches.Count > maxResults)
						break;
				}

				if (matches.Count == 0)
					return $"No string literals found matching '{SearchTerm}' in assembly {Assembly}.";

				var sb = new StringBuilder();
				foreach (var match in matches.Take(maxResults))
					sb.AppendLine(match);
				if (matches.Count > maxResults)
					sb.AppendLine($"... truncated to {maxResults} results");
				return sb.ToString();
			}
			catch (Exception ex) {
				return $"Exception: {ex.Message}";
			}
		}

		[Command("Get_Selected_Node", MCPCmdDescription = "Gets the currently selected node within dnSpyEx")]
		public static string GetCurrentlySelectItem() {
			try {
				// 1) we declare a local to capture the result
				object selected = null;

				// 2) marshal the read to the TreeView's UI thread
				var tv = Global.MyTreeView.TreeView;
				
				Global.MyAppWindow.MainWindow.Dispatcher.Invoke(() => {
					selected = tv.SelectedItem;
				});

				// 3) nothing selected?
				if (selected == null)
					return string.Empty;

				// 4) if it's a DocumentTreeNodeData (or one of its sub-interfaces), pull out a name
				if (selected is DocumentTreeNodeData docNode) {
					// e.g. for methods/types you might want the metadata name: //"dnSpy.Documents.TreeView.MethodNodeImpl"}
					if (docNode is dnSpy.Contracts.Documents.TreeView.AssemblyDocumentNode tn)
						return "Assembly Document: " + tn.NodePathName.Name;
					if (docNode is dnSpy.Contracts.Documents.TreeView.AssemblyReferenceNode an)
						return "Assembly Reference: " + an.NodePathName.Name;
					
					if (docNode is dnSpy.Contracts.Documents.TreeView.NamespaceNode nn)
						return "Namespace: " + nn.NodePathName.Name;
					if (docNode is dnSpy.Contracts.Documents.TreeView.TypeNode tyn)
						return "Class Type: " + tyn.NodePathName.Name;

					if (docNode is dnSpy.Contracts.Documents.TreeView.MethodNode mn)
						return "MethodNode: " + mn.NodePathName.Name;
					if (docNode is dnSpy.Contracts.Documents.TreeView.FieldNode fn)
						return "FieldNode: " + fn.NodePathName.Name;
					// …etc…
					// fallback:
					var type = docNode.GetType();
					return docNode.Icon.Name + ": " + docNode.NodePathName.Name;
				}

				// 5) otherwise just ToString()
				return selected.ToString()!;
			}
			catch (Exception ex) {
				return $"Exception: {ex.Message}";
			}
		}

		[Command("Get_Loaded_Assemblies", MCPCmdDescription = "Gets all Assemblys currently loaded within dnSpyEx")]
		public static string DumpLoadedAssemblies() {
			try {
				var assemblies = Global.MyTreeView.GetAllModuleNodes()
					.Select(modNode => modNode.GetModule())
					.GroupBy(module => module.Assembly?.Name?.String ?? module.Name.String)
					.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
					.ToList();

				if (assemblies.Count == 0)
					return "No assemblies are currently loaded in dnSpyEx.";

				var sb = new StringBuilder();
				sb.AppendLine("AssemblyCount: " + assemblies.Count);
				sb.AppendLine();

				foreach (var assemblyGroup in assemblies) {
					var modules = assemblyGroup.OrderBy(module => module.Name.String, StringComparer.OrdinalIgnoreCase).ToList();
					var typeCount = modules.Sum(module => module.GetTypes().Count());
					sb.AppendLine("Assembly: " + assemblyGroup.Key);
					sb.AppendLine("  ModuleCount: " + modules.Count);
					sb.AppendLine("  TypeCount: " + typeCount);
					foreach (var module in modules)
						sb.AppendLine("  Module: " + module.Name + " :: Location=" + (module.Location ?? "<memory>"));
					sb.AppendLine();
				}

				return sb.ToString().TrimEnd();
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		[Command("Namespaces_From_Assembly", MCPCmdDescription = "Dumps all unique namespaces under a given Assembly.")]
		public static string DumpNamespacesFromAssembly(string AssemblyName) {
			try {
				var modules = GetModuleNodes(AssemblyName).Select(node => node.GetModule()).ToList();
				if (modules.Count == 0)
					return $"Assembly {AssemblyName} is not loaded.";

				var namespaceGroups = modules
					.SelectMany(module => module.GetTypes())
					.GroupBy(type => string.IsNullOrWhiteSpace(type.Namespace.String) ? "<global>" : type.Namespace.String)
					.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
					.ToList();

				var sb = new StringBuilder();
				sb.AppendLine("Assembly: " + AssemblyName);
				sb.AppendLine("NamespaceCount: " + namespaceGroups.Count);
				sb.AppendLine();

				foreach (var group in namespaceGroups)
					sb.AppendLine("Namespace: " + group.Key + " :: types=" + group.Count());

				return sb.ToString().TrimEnd();
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		[Command("Get_Global_Namespaces", MCPCmdDescription = "List all types in the global namespace (i.e., no explicit namespace).")]
		public static string Get_Global_Namespaces() {
			try {
				var sb = new StringBuilder();
				Debug.WriteLine("- Global Namespace Types -");

				// Gather all TypeDefs from all modules
				var globalTypes = Global.MyTreeView
					.GetAllModuleNodes()
					.SelectMany(mod => mod.TreeNode.Data.GetModule().GetTypes())
					.Where(t => string.IsNullOrEmpty(t.Namespace))
					.OrderBy(t => t.FullName);

				// Output each type
				foreach (var type in globalTypes) {
					Debug.WriteLine($"	{type.FullName}");
					sb.AppendLine(type.FullName);
				}

				if (sb.Length == 0) {
					return "No types found in the global namespace.";
				}
				return sb.ToString();
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		[Command("Classes_From_Namespace", MCPCmdDescription = "List all Classes under a given Namespace.")]
		public static string DumpClassesFromNamespace(string AssemblyName, string Namespace) {
			try {
				var matches = EnumerateLoadedTypes(AssemblyName)
					.Where(info => string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase))
					.OrderBy(info => info.Type.FullName, StringComparer.OrdinalIgnoreCase)
					.ToList();

				if (matches.Count == 0)
					return $"No types found in namespace '{Namespace}' for assembly {AssemblyName}.";

				var sb = new StringBuilder();
				sb.AppendLine("Assembly: " + AssemblyName);
				sb.AppendLine("Namespace: " + (string.IsNullOrWhiteSpace(Namespace) ? "<global>" : Namespace));
				sb.AppendLine("TypeCount: " + matches.Count);
				sb.AppendLine();

				foreach (var match in matches)
					sb.AppendLine("Type: " + match.Type.FullName + " :: token=0x" + match.Type.MDToken.Raw.ToString("X8"));

				return sb.ToString().TrimEnd();
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}


		[Command("Get_Class_Sourcecode", MCPCmdDescription = "Dumps a target Class sourcecode")]
		public static string DumpClassCode(string Assembly, string Namespace, string ClassName) { //Dumps a Classes sourcecode
			try {
				string DataToReturn = "";
				//Debug.WriteLine("-MethodDef-");

				ModuleDef MyModuleDef;
				foreach (ModuleDocumentNode Modnode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					MyModuleDef = Modnode.GetModule();
					if (MyModuleDef.Assembly.Name == (Assembly)) {
						Debug.WriteLine("\t" + MyModuleDef.Name); //GemBox.Spreadsheet.dll
																  //Debug.WriteLine("\t" + Modnode.GetModule().Name);
																  //DataToReturn += Modnode.GetModule().Name + "\r\n";

						var ModNode = Modnode.TreeNode.Data.GetModuleNode();
						var ModTypes = Modnode.TreeNode.Data.GetModule().GetTypes().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToList();
						//DataToReturn += "\t" + DumpNode(Modnode.TreeNode, 1);
						foreach (TypeDef MyType in ModTypes) {
							Debug.WriteLine("\t" + MyType.FullName);
							if (MyType.Namespace == Namespace) {
								if (MyType.Name == ClassName) {
									if (string.IsNullOrEmpty(Namespace) ? MyType.FullName == ClassName : MyType.FullName.StartsWith(Namespace + "." + ClassName)) { //+MethodName
																																									//Debug.WriteLine(TheExtension.DumpSource(Modnode, MyType)); //The class as a whole
										DataToReturn += TheExtension.DumpSource(Modnode, MyType);
									}
								}
							}
						}
					}
				}
				return DataToReturn;
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		/*
		[Command("Get_Class_Sourcecode", MCPCmdDescription = "Dumps a target Class sourcecode")]
		public static string DumpClassCode(string Assembly, string Namespace, string ClassName) { //Dumps a Classes sourcecode
			try {
				string DataToReturn = "";
				//Debug.WriteLine("-MethodDef-");

				ModuleDef MyModuleDef;
				foreach (ModuleDocumentNode Modnode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					MyModuleDef = Modnode.GetModule();
					if (MyModuleDef.Assembly.Name==(Assembly)) {
						Debug.WriteLine("\t" + MyModuleDef.Name); //GemBox.Spreadsheet.dll
						//Debug.WriteLine("\t" + Modnode.GetModule().Name);
						//DataToReturn += Modnode.GetModule().Name + "\r\n";

						var ModNode = Modnode.TreeNode.Data.GetModuleNode();
						var ModTypes = Modnode.TreeNode.Data.GetModule().GetTypes().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToList();
						//DataToReturn += "\t" + DumpNode(Modnode.TreeNode, 1);
						foreach (TypeDef MyType in ModTypes) {
							Debug.WriteLine("\t" + MyType.FullName);
							if (MyType.Namespace == Namespace) {
								if (MyType.Name == ClassName) {
									if (string.IsNullOrEmpty(Namespace) ? MyType.FullName == ClassName : MyType.FullName.StartsWith(Namespace + "." + ClassName)) { //+MethodName
																								   //Debug.WriteLine(TheExtension.DumpSource(Modnode, MyType)); //The class as a whole
										DataToReturn += TheExtension.DumpSource(Modnode, MyType);
									}
								}
							}
						}
					}
				}
				return DataToReturn;
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}
		*/

		[Command("Get_Method_Prototypes", MCPCmdDescription = "List all Method prototypes from a givin Class within a given Namespace.")]
		public static string DumpMethodPrototypes(string Assembly, string Namespace, string ClassName) {
			try {
				var typeInfo = EnumerateLoadedTypes(Assembly)
					.FirstOrDefault(info =>
						string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase));

				if (typeInfo == null)
					return $"Type {Namespace}.{ClassName} was not found in assembly {Assembly}.";

				var methods = typeInfo.Type.Methods
					.OrderBy(method => method.Name.String, StringComparer.OrdinalIgnoreCase)
					.ToList();

				var sb = new StringBuilder();
				sb.AppendLine("Assembly: " + typeInfo.AssemblyName);
				sb.AppendLine("Module: " + typeInfo.Module.Name);
				sb.AppendLine("Type: " + typeInfo.Type.FullName);
				sb.AppendLine("MethodCount: " + methods.Count);
				sb.AppendLine();

				foreach (var method in methods)
					sb.AppendLine("Method: " + method.Name + " :: " + (method.MethodSig != null ? method.MethodSig.ToString() : "<no-signature>") + " :: token=0x" + method.MDToken.Raw.ToString("X8"));

				return sb.ToString().TrimEnd();
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		[Command("Get_Method_SourceCode", MCPCmdDescription = "Dumps a target Method's sourcecode")]
		public static string DumpMethodsSourcode(string Assembly, string Namespace, string ClassName, string MethodName) {
			try {
				string DataToReturn = "";
				//Debug.WriteLine("-MethodDef-");
				ModuleDef MyModuleDef;
				foreach (ModuleDocumentNode Modnode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					MyModuleDef = Modnode.GetModule();
					if (MyModuleDef.Assembly.Name == (Assembly)) {
						Debug.WriteLine("\t" + MyModuleDef.Name);
						//DataToReturn += MyModuleDef.Name + "\r\n";
						var ModTypes = Modnode.TreeNode.Data.GetModule().GetTypes().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToList();
						foreach (TypeDef MyType in ModTypes) {
							Debug.WriteLine("\t" + MyType.FullName);
							if (MyType.Namespace == Namespace) {
								if (MyType.Name == ClassName) {
									//DataToReturn += "\t" + MyType.FullName + "\r\n";
									List<MethodDef> Methods = MyType.Methods.OrderBy(t => t.Name.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
									foreach (MethodDef MyMethod in Methods) {
										if (MyMethod.Name == (MethodName)) {
											DataToReturn += TheExtension.DumpSource(Modnode, MyMethod);
										}
									}
								}
							}
						}
					}
				}
				return DataToReturn;
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		[Command("Update_Method_SourceCode", MCPCmdDescription = "Update a target Method's sourcecode using C#, Source argument Example: Console.WriteLine(\"Hello from patched method!\"); return \"TestedValue\";")]
		public static string Update_Methods_Sourcode(string Assembly, string Namespace, string ClassName, string MethodName, string Source) {
			try {
				string DataToReturn = "";
				//Debug.WriteLine("-MethodDef-");
				ModuleDef MyModuleDef;
				foreach (ModuleDocumentNode Modnode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					MyModuleDef = Modnode.GetModule();
					if (MyModuleDef.Assembly.Name == (Assembly)) {
						Debug.WriteLine("\t" + MyModuleDef.Name);
						//DataToReturn += MyModuleDef.Name + "\r\n";
						var ModTypes = Modnode.TreeNode.Data.GetModule().GetTypes().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToList();
						foreach (TypeDef MyType in ModTypes) {
							Debug.WriteLine("\t" + MyType.FullName);
							if (MyType.Namespace == Namespace) {
								if (MyType.Name == ClassName) {
									//DataToReturn += "\t" + MyType.FullName + "\r\n";
									List<MethodDef> Methods = MyType.Methods.OrderBy(t => t.Name.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
									foreach (MethodDef MyMethod in Methods) {
										if (MyMethod.Name == (MethodName)) {
											DataToReturn += TheExtension.UpdateSource(Modnode, MyMethod, Source);
										}
									}
								}
							}
						}
					}
				}
				return DataToReturn;
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		[Command("Get_Function_Opcodes", MCPCmdDescription = "Returns the IL opcodes of the specified method (with source line numbers)")]
		public static string Get_Function_Opcodes(string assemblyName, string @namespace, string className, string methodName) 
		{
			try {
				// scan every loaded module
				foreach (ModuleDocumentNode modNode in Global.MyTreeView.GetAllModuleNodes()) {
					var module = modNode.GetModule();
					if (!string.Equals(module.Assembly.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
						continue;

					// find the type
					var type = module.GetTypes()
									 .FirstOrDefault(t =>
										 t.Namespace == @namespace &&
										 t.Name == className
									 );
					if (type == null)
						continue;

					// find the method
					var method = type.Methods
									 .FirstOrDefault(m =>
										 string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)
									 );
					if (method == null)
						continue;

					// build the output
					var sb = new StringBuilder();
					sb.AppendLine($"// IL for {assemblyName}:{@namespace}.{className}.{methodName}");
					sb.AppendLine($"// #    Offset    OpCode     Operand");
					sb.AppendLine(new string('-', 70));

					int lineNo = 0;
					foreach (var instr in method.Body.Instructions) {
						lineNo++;
						// IL offset
						var offset = instr.Offset.ToString("X4");
						// mnemonic
						var opName = instr.OpCode.Name;
						// operand if present
						var operand = instr.Operand?.ToString() ?? "";

						sb.AppendLine(
							$"{lineNo,3}   {offset,-8} {opName,-10} {operand}"
						);
					}

					return sb.ToString();
				}

				return $"⚠️ Method {className}.{methodName} not found in assembly {assemblyName}";
			}
			catch (Exception ex) {
				return $"❌ Exception: {ex.Message}";
			}
		}

		[Command("Set_Function_Opcodes", MCPCmdDescription = @"Modifies the IL of the specified method at a given IL line index. mode = ""Overwrite"" → replaces existing instructions starting at that index mode = ""Append"" → inserts new instructions at that index, shifting old ones (IL lines are 0-based, so 0 means the very first instruction.). Example of IlOpcodes: ""Ldstr Hello, world!"",""Call System.Console::WriteLine(System.String)"",""Ret""")]
		public static string Set_Function_Opcodes(string assemblyName, string @namespace, string className, string methodName, string[] ilOpcodes, int ilLineNumber, string mode) {
			string DataToReturn = Global.MyAppWindow.MainWindow.Dispatcher.Invoke(() => Set_Function_Opcodes_Func(assemblyName, @namespace, className, methodName, ilOpcodes, ilLineNumber, mode)) as string;
			return DataToReturn;
		}

		public static string Set_Function_Opcodes_Func(string assemblyName, string @namespace, string className, string methodName, string[] ilOpcodes, int ilLineNumber, string mode) {
			try {
				foreach (ModuleDocumentNode modNode in Global.MyTreeView.GetAllModuleNodes()) {
					var module = modNode.GetModule();
					if (!string.Equals(module.Assembly.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
						continue;

					var type = module.GetTypes()
									 .FirstOrDefault(t => t.Namespace == @namespace && t.Name == className);
					if (type == null) continue;

					var method = type.Methods
									 .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
					if (method == null) continue;

					var instrs = method.Body.Instructions;
					int existingCount = instrs.Count;

					// allow 0 through existingCount (inclusive at start, exclusive at end)
					if (ilLineNumber < 0 || ilLineNumber > existingCount) {
						return $"⚠️ Cannot target IL line {ilLineNumber}: method has only {existingCount} instruction{(existingCount == 1 ? "" : "s")}, so valid range is 0–{existingCount}.";
					}

					// Build new IL instructions (unchanged)
					var injected = new List<Instruction>();
					var lineRe = new Regex(@"^\s*(\S+)(?:\s+(.+))?$");
					var callRe = new Regex(@"^(?:\S+\s+)?(?<type>[\w\.]+)::(?<method>\w+)\((?<params>.*)\)$");

					foreach (var raw in ilOpcodes) {
						var line = raw.Trim();
						if (string.IsNullOrEmpty(line) || line.StartsWith("//"))
							continue;

						var m = lineRe.Match(line);
						if (!m.Success) continue;

						var opName = m.Groups[1].Value.Trim();

						var normalized = opName.Replace('.', '_');

						var fld = typeof(OpCodes).GetField(normalized,
										 BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
						if (fld == null)
							return $"⚠️ Unknown OpCode '{opName}'";
						var code = (OpCode)fld.GetValue(null)!;
						var operandText = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";

						if (opName.Equals("Ldstr", StringComparison.OrdinalIgnoreCase)) {
							injected.Add(Instruction.Create(code, operandText));
						}
						else if (opName.Equals("Call", StringComparison.OrdinalIgnoreCase)) {
							var cm = callRe.Match(operandText);
							if (!cm.Success)
								return $"⚠️ Cannot parse Call operand '{operandText}'";

							var typeName = cm.Groups["type"].Value;
							var shortName = cm.Groups["method"].Value;
							var paramsSection = cm.Groups["params"].Value;
							var paramNames = string.IsNullOrWhiteSpace(paramsSection)
								 ? Array.Empty<string>()
								 : paramsSection.Split(',').Select(p => p.Trim()).ToArray();

							var targetType = Type.GetType(typeName, throwOnError: true);
							var candidates = targetType.GetMethods(
								BindingFlags.Public | BindingFlags.NonPublic |
								BindingFlags.Static | BindingFlags.Instance)
								.Where(mi => mi.Name == shortName).ToArray();
							if (candidates.Length == 0)
								return $"⚠️ No method '{shortName}' on '{typeName}'";

							var chosen = candidates.FirstOrDefault(mi => mi.GetParameters().Length == paramNames.Length)
										 ?? candidates[0];
							var iMethod = module.Import(chosen);
							injected.Add(Instruction.Create(code, iMethod));
						}
						else {
							injected.Add(Instruction.Create(code));
						}
					}

					// Splice into IL at exactly ilLineNumber
					int idx = ilLineNumber;
					if (mode.Equals("Overwrite", StringComparison.OrdinalIgnoreCase)) {
						for (int i = 0; i < injected.Count && idx < instrs.Count; i++)
							instrs.RemoveAt(idx);
					}
					for (int i = injected.Count - 1; i >= 0; i--)
						instrs.Insert(idx, injected[i]);

					Global.MyTreeView.TreeView.RefreshAllNodes();
					return $"✅ {(mode.Equals("Overwrite", StringComparison.OrdinalIgnoreCase) ? "Overwrote" : "Appended")} {injected.Count} instruction{(injected.Count == 1 ? "" : "s")} at IL line {ilLineNumber}.";
				}

				return $"⚠️ Method {className}.{methodName} not found in assembly {assemblyName}";
			}
			catch (Exception ex) {
				return $"❌ Exception: {ex.Message}";
			}
		}

		[Command("Overwrite_Full_Func_Opcodes", MCPCmdDescription = "Overwrites a whole method's ILcode with the provided opcode lines, all other code for this function is removed. ilOpcodes argument is an array of strings Ex. \"Ldstr Hello, world!\",\r\n\"Call System.Console:WriteLine\",\r\n\"Ret\"")]
		public static string Overwrite_Full_Function_Opcodes(string assemblyName, string @namespace, string className, string methodName, string[] ilOpcodes) 
		{
			string DataToReturn = Global.MyAppWindow.MainWindow.Dispatcher.Invoke(() => Overwrite_Full_Function_Opcodes_Func(assemblyName, @namespace, className, methodName, ilOpcodes)) as string;
			return DataToReturn;
		}

		public static string Overwrite_Full_Function_Opcodes_Func(string assemblyName, string @namespace, string className, string methodName, string[] ilOpcodes) 
		{
			try {
				foreach (ModuleDocumentNode modNode in Global.MyTreeView.GetAllModuleNodes()) {
					var module = modNode.GetModule();
					if (!string.Equals(module.Assembly.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
						continue;

					var type = module.GetTypes()
									 .FirstOrDefault(t => t.Namespace == @namespace && t.Name == className);
					if (type == null)
						continue;

					var method = type.Methods
									 .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
					if (method == null)
						continue;

					// Clear existing IL
					var instrs = method.Body.Instructions;
					instrs.Clear();

					var lineRe = new Regex(@"^\s*(\S+)(?:\s+(.+))?$");
					foreach (var raw in ilOpcodes) {
						var line = raw.Trim();
						if (string.IsNullOrEmpty(line) || line.StartsWith("//"))
							continue;

						var m = lineRe.Match(line);
						if (!m.Success)
							continue;

						var opName = m.Groups[1].Value;
						var operandText = m.Groups[2].Success ? m.Groups[2].Value : "";

						// reflect the OpCode
						var normalizedOpName = opName.Replace('.', '_');
						var fld = typeof(OpCodes).GetField(normalizedOpName,
							BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
						if (fld == null)
							return $"⚠️ Unknown OpCode '{opName}'";
						var code = (OpCode)fld.GetValue(null)!;

						// now create the instruction
						if (opName == "Ldstr") {
							instrs.Add(Instruction.Create(code, operandText));
						}
						else if (opName == "Call") {
							// operandText: "Namespace.Type:MethodName,arg1,arg2"
							// split off method lookup from parameter values
							var parts = operandText.Split(new[] { ':' }, 2);
							var typeName = parts[0];
							var rest = parts.Length > 1 ? parts[1] : "";
							var methodParts = rest.Split(',').Select(s => s.Trim()).ToArray();
							var methodShortName = methodParts[0];
							var paramValues = methodParts.Skip(1).ToArray();

							var targetType = Type.GetType(typeName, throwOnError: true);
							// find all same-name methods:
							var candidates = targetType.GetMethods(
								BindingFlags.Public | BindingFlags.NonPublic |
								BindingFlags.Static | BindingFlags.Instance
							).Where(mi => mi.Name == methodShortName).ToArray();

							MethodInfo chosen;
							// pick overload by matching parameter count
							var byCount = candidates.FirstOrDefault(mi =>
								mi.GetParameters().Length == paramValues.Length);
							if (byCount != null)
								chosen = byCount;
							else
								chosen = candidates.First(); // fallback

							// parse each operand to its CLR type:
							var parsedOperands = new object[paramValues.Length];
							var paramInfos = chosen.GetParameters();
							for (int i = 0; i < paramValues.Length; i++) {
								var piType = paramInfos[i].ParameterType;
								// only string support for now:
								if (piType == typeof(string))
									parsedOperands[i] = paramValues[i];
								else
									parsedOperands[i] = Convert.ChangeType(paramValues[i], piType);
							}

							// import the MethodInfo into the module
							var iMethod = module.Import(chosen);
							// and build the call instruction
							instrs.Add(Instruction.Create(code, iMethod));
							// if non-string params you'd follow with Ldarg or Ldc_ as needed,
							// but most user-supplied Call patches will be simple Console.WriteLine(string).
						}
						else {
							instrs.Add(Instruction.Create(code));
						}
					}

					// refresh the UI
					Global.MyTreeView.TreeView.RefreshAllNodes();
					return $"✅ Overwrote IL of {className}.{methodName}";
				}

				return $"⚠️ Method {className}.{methodName} not found in {assemblyName}";
			}
			catch (Exception ex) {
				return $"❌ Exception: {ex.Message}";
			}
		}

		[Command("Update_Tabs_View", MCPCmdDescription = "Update all active tabs to reflect any changes or adjustments.")]
		public static string RefreshAllOpenTabs() {
			string DataToReturn = Global.MyAppWindow.MainWindow.Dispatcher.Invoke(() => RefreshAllOpenTabs_func()) as string;
			return DataToReturn;
		}
		public static string RefreshAllOpenTabs_func() {
			try {
				// 1) Grab a snapshot of all currently open tabs
				var openTabs = Global.MyDocumentTabService.SortedTabs.ToList();
				Global.MyDocumentTabService.Refresh(openTabs);
				// 2) For each tab: remember its document, close it, then re-open it
				//foreach (var tab in openTabs) {
				//	IDocumentViewer doc = tab.TryGetDocumentViewer();
				//	// close the existing tab
				//	documentTabService.Refresh(doc.DocumentTab);
				//	documentTabService.Close(tab);
				//	// re-open it (activate:false so we don't steal focus)
				//	documentTabService.Refresh(doc, activate: false);
				//}
				return "Document tabs refreshed";
			}
			catch (Exception ex) {
				return "Exception " + ex.Message;
			}
		}

		[Command("Rename_Namespace", MCPCmdDescription = "Renames exactly one distinct namespace across all types.")]
		public static string RenameNamespace(string Assembly, string Old_Namespace_Name, string New_Namespace_Name) {
			try {
				if (string.IsNullOrWhiteSpace(Old_Namespace_Name))
					return "Old_Namespace_Name cannot be empty.";
				if (New_Namespace_Name == null)
					return "New_Namespace_Name cannot be null.";

				var allTypes = GetModuleNodes(Assembly)
					.SelectMany(modNode => modNode.GetModule().GetTypes())
					.Where(type => string.Equals(type.Namespace ?? string.Empty, Old_Namespace_Name, StringComparison.Ordinal))
					.ToList();

				if (allTypes.Count == 0)
					return $"No namespace found matching '{Old_Namespace_Name}' in assembly {Assembly}.";

				foreach (var type in allTypes)
					type.Namespace = New_Namespace_Name;

				return $"Namespace '{Old_Namespace_Name}' renamed to '{New_Namespace_Name}' in {allTypes.Count} types.";
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
			
		}

		[Command("Rename_Class", MCPCmdDescription = "Renames a specific class within a given Namespace.")]
		public static string RenameClass(string Assembly, string Namespace, string OldClassName, string NewClassName) { //Not validated yet
			try {
				var matches = GetModuleNodes(Assembly)
					.SelectMany(modNode => modNode.GetModule().GetTypes())
					.Where(type =>
						string.Equals(type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(type.Name.String, OldClassName, StringComparison.Ordinal))
					.ToList();

				if (matches.Count == 1) {
					var type = matches[0];
					type.Name = NewClassName;
					return $"{Namespace}.{OldClassName} renamed to {NewClassName} successfully";
				}
				else if (matches.Count == 0) {
					return $"No classes found matching '{OldClassName}' in namespace {Namespace}.";
				}
				else {
					var found = string.Join(", ", matches.Select(t => t.FullName));
					return $"Multiple classes found matching '{OldClassName}': {found}. Be more specific, Rename aborted.";
				}
			}
			catch(Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		[Command("Rename_Method", MCPCmdDescription = "Renames a specific method by class within a given namespace. Pass MethodSignature to disambiguate overloads.")]
		public static string RenameMethod(string Assembly, string Namespace, string ClassName, string MethodName, string Newname, string MethodSignature = null) {
			try 
			{ 
				var matches = GetModuleNodes(Assembly)
					.SelectMany(modNode => modNode.GetModule().GetTypes())
					.Where(type =>
						string.Equals(type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase))
					.SelectMany(type => type.Methods)
					.Where(method =>
						string.Equals(method.Name.String, MethodName, StringComparison.Ordinal) &&
						MatchesMethodSignature(method, MethodSignature))
					.ToList();

				// decide based on how many matches we got
				if (matches.Count == 1) {
					matches[0].Name = Newname;
					return $"{Namespace}->{ClassName}->{MethodName} renamed to {Newname} successfully";
				}
				else if (matches.Count == 0) {
					return $"No Methods found matching '{MethodName}' in {Namespace}.{ClassName}.";
				}
				else {
					// list the ambiguous matches
					var found = string.Join(", ", matches.Select(m => m.FullName));
					return $"Multiple Methods found matching '{MethodName}': {found}. Pass MethodSignature to disambiguate. Rename aborted.";
				}
			}
			catch (Exception ex) {
				return $"Exception: " + ex.Message;
			}
		}

		public static string PatchMethodLogEntry(string assemblyName, string @namespace, string className, string methodName) {
			try {
				foreach (ModuleDocumentNode modNode in Global.MyTreeView.GetAllModuleNodes()) {
					var module = modNode.GetModule();
					if (!string.Equals(module.Assembly.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
						continue;

					// find your type
					var type = module.GetTypes()
									 .FirstOrDefault(t => t.Namespace == @namespace && t.Name == className);
					if (type == null)
						continue;

					// find your method
					var method = type.Methods
									 .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
					if (method == null)
						continue;

					// 1) import Console.WriteLine(string) into this module
					var writeLineRef = module.Import(
						typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(string) })
					);

					// 2) inject at the top of the method
					IList<Instruction> instructions = method.Body.Instructions;
					instructions.Insert(0, Instruction.Create(OpCodes.Ldstr, $"[dnSpyPatch] Entering {methodName}"));
					instructions.Insert(1, Instruction.Create(OpCodes.Call, writeLineRef));

					Global.MyTreeView.TreeView.RefreshAllNodes();

					return $"✅ Successfully patched {methodName} in {className}";
				}

				return $"⚠️ Could not find method {className}.{methodName} in assembly {assemblyName}";
			}
			catch (Exception ex) {
				return $"❌ Exception: {ex.Message}";
			}
		}

		//[Command("Dump.Method.From.Class", MCPCmdDescription = "Dumps all Methods by Class within a given Namespace. Set 'DumpCode' = true to view the code within the Class as a whole.")]
		public static string DumpClasses(string Assembly, string Namespace, string ClassName, bool DumpMethods = false, bool DumpCode = false) { //Dumps a Class and its Methods
			string DataToReturn = "";
			//Debug.WriteLine("-MethodDef-");

			ModuleDef MyModuleDef;
			foreach (ModuleDocumentNode Modnode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
				MyModuleDef = Modnode.GetModule();
				if (MyModuleDef.Name.Contains(Assembly)) {
					Debug.WriteLine("\t" + MyModuleDef.Name); //GemBox.Spreadsheet.dll
					if (MyModuleDef.Name.ToString().Contains(Namespace)) {
						//Debug.WriteLine("\t" + Modnode.GetModule().Name);
						if (!DumpCode) {
							DataToReturn += Modnode.GetModule().Name + "\r\n";
						}

						var ModNode = Modnode.TreeNode.Data.GetModuleNode();
						var ModTypes = Modnode.TreeNode.Data.GetModule().GetTypes().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToList();
						//DataToReturn += "\t" + DumpNode(Modnode.TreeNode, 1);
						foreach (TypeDef MyType in ModTypes) {
							Debug.WriteLine("\t" + MyType.FullName);
							if (MyType.FullName.StartsWith(Namespace + "." + ClassName)) { //+MethodName
								if (!DumpCode) {
									if (ModNode != null) {
										//Debug.WriteLine("\t\t" + MyType.FullName); //The Class
										DataToReturn += "\t" + MyType.FullName + "\r\n";
										//Debug.WriteLine(DumpSource(modNode, MyType)); //The class as a whole
									}
									if (DumpMethods) {
										List<MethodDef> Methods = MyType.Methods.OrderBy(t => t.Name.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
										foreach (MethodDef MyMethod in Methods) {
											//Debug.WriteLine("\t\t\t" + MyMethod.FullName); //The specific Method, Way too much data
											DataToReturn += "\t\t" + MyMethod.FullName + "\r\n";
										}
									}

								}
								if (DumpCode) {
									//Debug.WriteLine(TheExtension.DumpSource(Modnode, MyType)); //The class as a whole
									DataToReturn += TheExtension.DumpSource(Modnode, MyType);
								}
							}
						}
					}
				}
			}
			return DataToReturn;
		}

		//[Command("Dump.Specific.Method", MCPCmdDescription = "Dumps all Methods by Class within a given Namespace. Set 'DumpCode' = true to view the code within the Class as a whole.")]
		public static string DumpMethods(string Assembly, string Namespace, string ClassName, string MethodName, bool DumpCode = false) {
			string DataToReturn = "";
			//Debug.WriteLine("-MethodDef-");

			ModuleDef MyModuleDef;
			foreach (ModuleDocumentNode Modnode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
				MyModuleDef = Modnode.GetModule();
				if (MyModuleDef.Name.Contains(Assembly)) {
					if (MyModuleDef.Name.ToString().Contains(Namespace)) {
						Debug.WriteLine("\t" + MyModuleDef.Name);
						DataToReturn += MyModuleDef.Name + "\r\n";
						var ModTypes = Modnode.TreeNode.Data.GetModule().GetTypes().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToList();
						foreach (TypeDef MyType in ModTypes) {
							Debug.WriteLine("\t" + MyType.FullName);
							if (MyType.FullName.StartsWith(Namespace + "." + ClassName)) { //+MethodName
								if (!DumpCode) {
									DataToReturn += "\t" + MyType.FullName + "\r\n";
								}
								//.OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
								List<MethodDef> Methods = MyType.Methods.OrderBy(t => t.Name.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
								foreach (MethodDef MyMethod in Methods) {
									if (MyMethod.Name.Contains(MethodName)) {
										//Debug.WriteLine("\t\t" + MyType.FullName);
										if (!DumpCode) {
											//Debug.WriteLine("\t\t\t" + MyMethod.FullName); //The specific Method, Way too much data
											DataToReturn += "\t\t" + MyMethod.FullName + "\r\n";
										}
										if (DumpCode) {
											DataToReturn += TheExtension.DumpSource(Modnode, MyMethod);
										}
									}
								}
							}
						}
					}
				}
			}
			return DataToReturn;
		}

		/// <summary>
		/// Walks *any* ITreeNode subtree, printing out:
		//   • Module.Name (if any)
		/// • "MethodDef" if there's a Method at that node
		/// and recurses into children with increasing indentation.
		/// </summary>
		public string DumpAllNodeClassesAndMethodsOld(ITreeNode node, int indentLevel = 0) {
			var indent = new string('\t', indentLevel);

			ModuleDef moduleDef = node.Data.GetModule();
			//var asmNode = node.Data.GetAssemblyNode();
			//var docNode = node.Data.GetDocumentNode(); //Doc and Mod seem to be the same
			var modNode = node.Data.GetModuleNode();
			//ITreeNode TN = node.Data.TreeNode;
			//if (asmNode != null && docNode != null && modNode != null)
			//	Debug.WriteLine($"{indent}{asmNode.NodePathName} {docNode.NodePathName} {modNode.NodePathName}");

			//Debug.WriteLine(TN.Data.Text);
			//DumpSource(modNode, moduleDef);

			foreach (MemberRef MyRef in moduleDef.GetMemberRefs().ToList()) {
				//Debug.WriteLine(MyRef.FullName + "-" + MyRef.Signature.ToString() + "-" + MyRef.GetParamCount());
			}

			foreach (ModuleRef MyRef in moduleDef.GetModuleRefs().ToList()) {
				//Debug.WriteLine(MyRef.FullName);
			}

			foreach (AssemblyRef MyAsmRef in moduleDef.GetAssemblyRefs().ToList()) {
				//Debug.WriteLine(MyAsmRef.FullName);
			}

			var ModTypes = moduleDef.GetTypes().ToList();
			foreach (TypeDef MyType in ModTypes) {
				//Debug.WriteLine(indent + "\t" + MyType.FullName);
				if (MyType.FullName.StartsWith("CNETTrafficFighterWeb")) {
					if (modNode != null) {
						Debug.WriteLine(indent + "\t" + MyType.FullName); //The Class
																		  //Debug.WriteLine(DumpSource(modNode, MyType)); //The class as a whole
					}
				}

				foreach (MethodDef MyMethod in MyType.Methods) {
					Debug.WriteLine(indent + "\t\t" + MyMethod.FullName); //The specific Method, Way too much data
				}
			}

			//foreach (ITreeNode child in node.Children.ToList()) {
			//	DumpNode(child, indentLevel + 1);
			//	List<ITreeNode> mychildren = child.Descendants().ToList();
			//	//Debug.WriteLine(mychildren.Count);	
			//}
			return "";
		}

		/*
		[Command("Dump.All", MCPCmdDescription = "Dumps all Classes and Methods within those classes by Namespace.")]
		public static string DumpAllNamespaceClassesAndMethods(string NamespaceAssemblyName) {
			string DataToReturn = "";
			Debug.WriteLine("-MethodDef-");

			foreach (ModuleDocumentNode Modnode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
				if (Modnode.GetModule().Name.ToString().Contains(NamespaceAssemblyName)) {
					Debug.WriteLine("\t" + Modnode.GetModule().Name);
					DataToReturn += Modnode.GetModule().Name + "\r\n";
					var ModNode = Modnode.TreeNode.Data.GetModuleNode();
					var ModTypes = Modnode.TreeNode.Data.GetModule().GetTypes().ToList();
					//DataToReturn += "\t" + DumpNode(Modnode.TreeNode, 1);

					foreach (TypeDef MyType in ModTypes) {
						//Debug.WriteLine(indent + "\t" + MyType.FullName);
						if (ModNode != null) { //parent module can be located?
							Debug.WriteLine("\t\t" + MyType.FullName); //The Class
							DataToReturn += ("\t" + MyType.FullName + "\r\n");
							//Debug.WriteLine(DumpSource(modNode, MyType)); //The class as a whole
						}
						foreach (MethodDef MyMethod in MyType.Methods) {
							Debug.WriteLine("\t\t\t" + MyMethod.FullName); //The specific Method, Way too much data
							DataToReturn += ("\t\t" + MyType.FullName + "\r\n");
						}
					}
				}
			}
			return DataToReturn;
		}
		*/

	}
}
