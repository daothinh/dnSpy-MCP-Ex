using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Resources;
using dnSpy.Contracts.Documents.TreeView;
using static Example1.Extension.SimpleMcpServer;

namespace Example1.Extension {
	partial class MCPCommands {
		sealed class LoadedTypeInfo {
			public string AssemblyName { get; set; }
			public ModuleDef Module { get; set; }
			public TypeDef Type { get; set; }
		}

		sealed class LoadedMethodInfo {
			public string AssemblyName { get; set; }
			public ModuleDef Module { get; set; }
			public TypeDef Type { get; set; }
			public MethodDef Method { get; set; }
		}

		sealed class AttributeSearchHit {
			public string AssemblyName { get; set; }
			public string TargetKind { get; set; }
			public string TargetId { get; set; }
			public CustomAttribute Attribute { get; set; }
		}

		sealed class CommandCatalogEntry {
			public string Name { get; set; }
			public string Category { get; set; }
			public string Description { get; set; }
			public string Parameters { get; set; }
			public bool IsLegacy { get; set; }
		}

		static IEnumerable<LoadedTypeInfo> EnumerateLoadedTypes(string assemblyName = null) =>
			GetModuleNodes(assemblyName)
				.SelectMany(modNode => modNode.GetModule().GetTypes().Select(type => new LoadedTypeInfo {
					AssemblyName = modNode.GetModule().Assembly?.Name ?? modNode.GetModule().Name,
					Module = modNode.GetModule(),
					Type = type,
				}));

		static IEnumerable<LoadedMethodInfo> EnumerateLoadedMethods(string assemblyName = null) =>
			EnumerateLoadedTypes(assemblyName)
				.SelectMany(typeInfo => typeInfo.Type.Methods.Select(method => new LoadedMethodInfo {
					AssemblyName = typeInfo.AssemblyName,
					Module = typeInfo.Module,
					Type = typeInfo.Type,
					Method = method,
				}));

		static bool TryParseMetadataToken(string tokenText, out uint token, out string error) {
			token = 0;
			error = null;

			if (string.IsNullOrWhiteSpace(tokenText)) {
				error = "Token cannot be empty.";
				return false;
			}

			var trimmed = tokenText.Trim();
			if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				trimmed = trimmed.Substring(2);

			if (uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out token))
				return true;

			if (uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out token))
				return true;

			error = "Token must be a decimal value or hexadecimal value such as 0x06000001.";
			return false;
		}

		static bool MatchesMethodSignature(MethodDef method, string methodSignature) {
			if (string.IsNullOrWhiteSpace(methodSignature))
				return true;

			var signatureText = method.MethodSig != null ? method.MethodSig.ToString() : string.Empty;
			return ContainsIgnoreCase(method.FullName, methodSignature) ||
				ContainsIgnoreCase(signatureText, methodSignature);
		}

		static bool MetadataEquals(MethodDef method, IMethod candidate) {
			if (method == null || candidate == null)
				return false;

			var resolved = candidate.ResolveMethodDef();
			if (resolved != null)
				return resolved.MDToken.Raw == method.MDToken.Raw && ReferenceEquals(resolved.Module, method.Module);

			return string.Equals(candidate.FullName, method.FullName, StringComparison.OrdinalIgnoreCase);
		}

		static bool MetadataEquals(TypeDef type, ITypeDefOrRef candidate) {
			if (type == null || candidate == null)
				return false;

			var resolved = candidate.ResolveTypeDef();
			if (resolved != null)
				return resolved.MDToken.Raw == type.MDToken.Raw && ReferenceEquals(resolved.Module, type.Module);

			return string.Equals(candidate.FullName, type.FullName, StringComparison.OrdinalIgnoreCase);
		}

		static bool MetadataEquals(TypeDef type, IField candidate) {
			if (type == null || candidate == null)
				return false;

			var fieldDef = candidate.ResolveFieldDef();
			if (fieldDef != null && fieldDef.DeclaringType != null)
				return fieldDef.DeclaringType.MDToken.Raw == type.MDToken.Raw && ReferenceEquals(fieldDef.DeclaringType.Module, type.Module);

			var declaringType = candidate.DeclaringType;
			return declaringType != null && MetadataEquals(type, declaringType);
		}

		static bool InstructionReferencesMethod(Instruction instruction, MethodDef targetMethod) {
			if (instruction?.Operand == null || targetMethod == null)
				return false;

			if (instruction.Operand is MethodSpec methodSpec)
				return MetadataEquals(targetMethod, methodSpec.Method);

			if (instruction.Operand is IMethod methodOperand)
				return MetadataEquals(targetMethod, methodOperand);

			return false;
		}

		static bool InstructionReferencesType(Instruction instruction, TypeDef targetType) {
			if (instruction?.Operand == null || targetType == null)
				return false;

			if (instruction.Operand is ITypeDefOrRef typeOperand)
				return MetadataEquals(targetType, typeOperand);

			if (instruction.Operand is IMethod methodOperand)
				return methodOperand.DeclaringType != null && MetadataEquals(targetType, methodOperand.DeclaringType);

			if (instruction.Operand is IField fieldOperand)
				return MetadataEquals(targetType, fieldOperand);

			return false;
		}

		static string FormatMethodIdentity(string assemblyName, MethodDef method) {
			var signatureText = method.MethodSig != null ? method.MethodSig.ToString() : "<no-signature>";
			return assemblyName + " :: " + method.DeclaringType.FullName + " :: " + method.Name + " :: " + signatureText;
		}

		static string FormatTypeIdentity(string assemblyName, TypeDef type) =>
			assemblyName + " :: " + type.FullName + " :: token=0x" + type.MDToken.Raw.ToString("X8");

		static string FormatAttributeArgument(CAArgument argument) {
			if (argument.Value == null)
				return "null";

			if (argument.Value is UTF8String utf8)
				return utf8.String ?? string.Empty;

			if (argument.Value is IList<CAArgument> arrayArgs) {
				var values = arrayArgs.Select(FormatAttributeArgument).ToArray();
				return "[" + string.Join(", ", values) + "]";
			}

			return argument.Value.ToString();
		}

		static string FormatCustomAttribute(CustomAttribute attribute) {
			var ctorType = attribute.AttributeType?.FullName ?? "<unknown>";
			var ctorArgs = attribute.ConstructorArguments.Count == 0
				? string.Empty
				: string.Join(", ", attribute.ConstructorArguments.Select(FormatAttributeArgument));
			var namedArgs = attribute.NamedArguments.Count == 0
				? string.Empty
				: string.Join(", ", attribute.NamedArguments.Select(arg => arg.Name + "=" + FormatAttributeArgument(arg.Argument)));

			var sb = new StringBuilder();
			sb.Append(ctorType).Append("(").Append(ctorArgs).Append(")");
			if (!string.IsNullOrWhiteSpace(namedArgs))
				sb.Append(" { ").Append(namedArgs).Append(" }");
			return sb.ToString();
		}

		static IEnumerable<AttributeSearchHit> EnumerateCustomAttributeHits(string assemblyName = null) {
			foreach (var modNode in GetModuleNodes(assemblyName)) {
				var module = modNode.GetModule();
				var asmName = module.Assembly?.Name ?? module.Name;

				if (module.Assembly != null) {
					foreach (var attribute in module.Assembly.CustomAttributes)
						yield return new AttributeSearchHit { AssemblyName = asmName, TargetKind = "Assembly", TargetId = module.Assembly.FullName, Attribute = attribute };
				}

				foreach (var attribute in module.CustomAttributes)
					yield return new AttributeSearchHit { AssemblyName = asmName, TargetKind = "Module", TargetId = module.FullName, Attribute = attribute };

				foreach (var type in module.GetTypes()) {
					foreach (var attribute in type.CustomAttributes)
						yield return new AttributeSearchHit { AssemblyName = asmName, TargetKind = "Type", TargetId = type.FullName, Attribute = attribute };

					foreach (var field in type.Fields) {
						foreach (var attribute in field.CustomAttributes)
							yield return new AttributeSearchHit { AssemblyName = asmName, TargetKind = "Field", TargetId = field.FullName, Attribute = attribute };
					}

					foreach (var property in type.Properties) {
						foreach (var attribute in property.CustomAttributes)
							yield return new AttributeSearchHit { AssemblyName = asmName, TargetKind = "Property", TargetId = property.FullName, Attribute = attribute };
					}

					foreach (var evt in type.Events) {
						foreach (var attribute in evt.CustomAttributes)
							yield return new AttributeSearchHit { AssemblyName = asmName, TargetKind = "Event", TargetId = evt.FullName, Attribute = attribute };
					}

					foreach (var method in type.Methods) {
						foreach (var attribute in method.CustomAttributes)
							yield return new AttributeSearchHit { AssemblyName = asmName, TargetKind = "Method", TargetId = method.FullName, Attribute = attribute };

						foreach (var parameter in method.Parameters) {
							foreach (var attribute in parameter.ParamDef?.CustomAttributes ?? Enumerable.Empty<dnlib.DotNet.CustomAttribute>())
								yield return new AttributeSearchHit { AssemblyName = asmName, TargetKind = "Parameter", TargetId = method.FullName + " :: " + parameter.Name, Attribute = attribute };
						}
					}
				}
			}
		}

		static string GetResourcePreview(EmbeddedResource resource, int maxBytes) {
			var bytes = resource.CreateReader().ToArray();
			var limited = bytes.Take(Math.Max(1, Math.Min(maxBytes, bytes.Length))).ToArray();
			var printableBytes = limited.Count(b => b == 9 || b == 10 || b == 13 || (b >= 32 && b <= 126));

			if (limited.Length > 0 && printableBytes >= limited.Length * 0.8)
				return Encoding.UTF8.GetString(limited);

			return string.Join(" ", limited.Select(b => b.ToString("X2")));
		}

		static string BuildAssemblyMap(
			string assemblyName,
			string namespaceFilter = null,
			string typeFilter = null,
			bool includeMethods = true,
			bool includeMethodSignatures = false,
			bool includeTokens = true,
			int maxNamespaces = 200,
			int maxTypesPerNamespace = 200,
			int maxMethodsPerType = 100) {
			var moduleNodes = GetModuleNodes(assemblyName).ToList();
			if (moduleNodes.Count == 0)
				return "Assembly " + assemblyName + " is not loaded.";

			maxNamespaces = NormalizeMaxResults(maxNamespaces, 200, 1000);
			maxTypesPerNamespace = NormalizeMaxResults(maxTypesPerNamespace, 200, 1000);
			maxMethodsPerType = NormalizeMaxResults(maxMethodsPerType, 100, 1000);

			var allTypes = moduleNodes
				.SelectMany(modNode => modNode.GetModule().GetTypes().Select(type => new {
					Module = modNode.GetModule(),
					Type = type,
					NamespaceName = string.IsNullOrWhiteSpace(type.Namespace.String) ? "<global>" : type.Namespace.String,
				}))
				.Where(entry => string.IsNullOrWhiteSpace(namespaceFilter) || ContainsIgnoreCase(entry.NamespaceName, namespaceFilter))
				.Where(entry => string.IsNullOrWhiteSpace(typeFilter) || ContainsIgnoreCase(entry.Type.FullName, typeFilter) || ContainsIgnoreCase(entry.Type.Name.String, typeFilter))
				.ToList();

			if (allTypes.Count == 0)
				return "No matching namespaces or types were found in assembly " + assemblyName + ".";

			var grouped = allTypes
				.GroupBy(entry => entry.NamespaceName)
				.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
				.Take(maxNamespaces + 1)
				.ToList();

			var sb = new StringBuilder();
			sb.AppendLine("Assembly: " + assemblyName);
			sb.AppendLine("Modules: " + string.Join(", ", moduleNodes.Select(node => node.GetModule().Name.String).Distinct(StringComparer.OrdinalIgnoreCase)));
			sb.AppendLine("NamespaceCount: " + grouped.Take(maxNamespaces).Count());
			sb.AppendLine();

			foreach (var nsGroup in grouped.Take(maxNamespaces)) {
				var types = nsGroup
					.OrderBy(entry => entry.Type.FullName, StringComparer.OrdinalIgnoreCase)
					.Take(maxTypesPerNamespace + 1)
					.ToList();

				sb.AppendLine("Namespace: " + nsGroup.Key + " (TypeCount=" + nsGroup.Count() + ")");
				foreach (var entry in types.Take(maxTypesPerNamespace)) {
					var type = entry.Type;
					sb.Append("  Type: ").Append(type.FullName);
					if (includeTokens)
						sb.Append(" :: token=0x").Append(type.MDToken.Raw.ToString("X8"));
					sb.Append(" :: methods=").Append(type.Methods.Count);
					sb.AppendLine();

					if (!includeMethods)
						continue;

					var methods = type.Methods
						.OrderBy(method => method.Name.String, StringComparer.OrdinalIgnoreCase)
						.Take(maxMethodsPerType + 1)
						.ToList();
					foreach (var method in methods.Take(maxMethodsPerType)) {
						sb.Append("    Method: ").Append(method.Name);
						if (includeMethodSignatures)
							sb.Append(" :: ").Append(method.MethodSig != null ? method.MethodSig.ToString() : "<no-signature>");
						if (includeTokens)
							sb.Append(" :: token=0x").Append(method.MDToken.Raw.ToString("X8"));
						sb.AppendLine();
					}

					if (methods.Count() > maxMethodsPerType)
						sb.AppendLine("    ... methods truncated to " + maxMethodsPerType);
				}

				if (types.Count() > maxTypesPerNamespace)
					sb.AppendLine("  ... types truncated to " + maxTypesPerNamespace);
				sb.AppendLine();
			}

			if (grouped.Count() > maxNamespaces)
				sb.AppendLine("... namespaces truncated to " + maxNamespaces);

			return sb.ToString().TrimEnd();
		}

		static string CategorizeCommand(string commandName) {
			if (commandName.StartsWith("Dbg_", StringComparison.OrdinalIgnoreCase))
				return "Debugger";
			if (commandName.StartsWith("Rename_", StringComparison.OrdinalIgnoreCase) ||
				commandName.StartsWith("Update_", StringComparison.OrdinalIgnoreCase) ||
				ContainsIgnoreCase(commandName, "Opcodes"))
				return "Mutation";
			if (ContainsIgnoreCase(commandName, "Source") ||
				ContainsIgnoreCase(commandName, "Prototypes"))
				return "Decompile";
			if (ContainsIgnoreCase(commandName, "Resource") ||
				ContainsIgnoreCase(commandName, "Attribute") ||
				ContainsIgnoreCase(commandName, "Reference") ||
				ContainsIgnoreCase(commandName, "Call") ||
				ContainsIgnoreCase(commandName, "Hierarchy") ||
				ContainsIgnoreCase(commandName, "Token"))
				return "Analysis";
			if (ContainsIgnoreCase(commandName, "Server") || commandName == "Help")
				return "Server";
			return "Discovery";
		}

		static IEnumerable<CommandCatalogEntry> GetCommandCatalog() {
			return typeof(MCPCommands)
				.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.SelectMany(method => method.GetCustomAttributes<CommandAttribute>().Select(attr => new CommandCatalogEntry {
					Name = attr.Name,
					Category = CategorizeCommand(attr.Name),
					Description = string.IsNullOrWhiteSpace(attr.MCPCmdDescription) ? "No description." : attr.MCPCmdDescription.Trim(),
					Parameters = string.Join(", ", method.GetParameters().Select(param => {
						var typeName = param.ParameterType.IsArray
							? param.ParameterType.GetElementType().Name + "[]"
							: param.ParameterType.Name;
						return param.Name + ":" + typeName + (param.IsOptional ? "=" + (param.DefaultValue ?? "null") : string.Empty);
					})),
					IsLegacy = attr.Name.StartsWith("Dump.", StringComparison.OrdinalIgnoreCase) || attr.Name == "Dump.All",
				}))
				.OrderBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
				.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
		}

		internal static string BuildDynamicHelp() {
			var catalog = GetCommandCatalog().ToList();
			var sb = new StringBuilder();
			sb.AppendLine("dnSpy MCP command catalog");
			sb.AppendLine("Use exact parameter names. Assembly/Namespace/Class/Method arguments are case-insensitive unless dnlib metadata requires an exact signature filter.");
			sb.AppendLine();
			sb.AppendLine("Summary:");
			sb.AppendLine("  TotalCommands: " + catalog.Count);
			sb.AppendLine("  DebuggerCommands: " + catalog.Count(entry => entry.Category == "Debugger"));
			sb.AppendLine("  StaticAnalysisCommands: " + catalog.Count(entry => entry.Category == "Analysis" || entry.Category == "Discovery" || entry.Category == "Decompile"));
			sb.AppendLine("  MutationCommands: " + catalog.Count(entry => entry.Category == "Mutation"));
			sb.AppendLine("  LegacyCommands: " + catalog.Count(entry => entry.IsLegacy));
			sb.AppendLine();

			foreach (var group in catalog.GroupBy(entry => entry.Category).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)) {
				sb.AppendLine("[" + group.Key + "]");
				foreach (var entry in group) {
					sb.Append("  ").Append(entry.Name);
					if (!string.IsNullOrWhiteSpace(entry.Parameters))
						sb.Append(" (").Append(entry.Parameters).Append(")");
					if (entry.IsLegacy)
						sb.Append(" [legacy]");
					sb.AppendLine();
					sb.AppendLine("    " + entry.Description);
				}
				sb.AppendLine();
			}

			sb.AppendLine("Suggested workflow:");
			sb.AppendLine("  1. Get_Loaded_Assemblies -> Search_Types/Search_Methods");
			sb.AppendLine("  2. Get_Type_Details/Get_Method_Details -> Find_Callers/Find_Callees/Search_References_To_Type");
			sb.AppendLine("  3. Dbg_Get_Status -> Dbg_List_Threads -> Dbg_Get_Thread_Frames -> Dbg_Evaluate_Batch");
			return sb.ToString();
		}

		[Command("Get_Tool_Inventory", MCPCmdDescription = "Returns the full MCP tool inventory grouped by category, including counts and parameter summaries.")]
		public static string GetToolInventory() => BuildDynamicHelp();

		[Command("Get_Method_Details", MCPCmdDescription = "Returns token, signature, implementation flags, body stats, parameters, locals and custom attributes for a target method.")]
		public static string GetMethodDetails(string Assembly, string Namespace, string ClassName, string MethodName, string MethodSignature = null) {
			try {
				var matches = EnumerateLoadedMethods(Assembly)
					.Where(info =>
						string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Method.Name.String, MethodName, StringComparison.OrdinalIgnoreCase) &&
						MatchesMethodSignature(info.Method, MethodSignature))
					.ToList();

				if (matches.Count == 0)
					return "Method was not found. Use Search_Methods or pass MethodSignature to disambiguate overloads.";
				if (matches.Count > 1)
					return "Multiple overloads matched: " + string.Join(" | ", matches.Select(match => match.Method.FullName));

				var matchInfo = matches[0];
				var method = matchInfo.Method;
				var sb = new StringBuilder();
				sb.AppendLine("Assembly: " + matchInfo.AssemblyName);
				sb.AppendLine("Module: " + matchInfo.Module.Name);
				sb.AppendLine("DeclaringType: " + method.DeclaringType.FullName);
				sb.AppendLine("Name: " + method.Name);
				sb.AppendLine("FullName: " + method.FullName);
				sb.AppendLine("Token: 0x" + method.MDToken.Raw.ToString("X8"));
				sb.AppendLine("Signature: " + (method.MethodSig != null ? method.MethodSig.ToString() : "<none>"));
				sb.AppendLine("RVA: 0x" + method.RVA.ToString("X"));
				sb.AppendLine("HasBody: " + method.HasBody);
				sb.AppendLine("InstructionCount: " + (method.HasBody ? method.Body.Instructions.Count.ToString() : "0"));
				sb.AppendLine("ExceptionHandlerCount: " + (method.HasBody ? method.Body.ExceptionHandlers.Count.ToString() : "0"));
				sb.AppendLine("ImplFlags: " + method.ImplAttributes);
				sb.AppendLine("Attributes: " + method.Attributes);
				sb.AppendLine("IsConstructor: " + method.IsConstructor);
				sb.AppendLine("IsVirtual: " + method.IsVirtual);
				sb.AppendLine("IsStatic: " + method.IsStatic);
				sb.AppendLine("GenericParameters: " + method.GenericParameters.Count);
				sb.AppendLine();
				sb.AppendLine("Parameters:");
				if (method.Parameters.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					for (int i = 0; i < method.Parameters.Count; i++) {
						var parameter = method.Parameters[i];
						sb.AppendLine("  [" + i + "] " + parameter.Type.FullName + " " + parameter.Name);
					}
				}

				sb.AppendLine();
				sb.AppendLine("Locals:");
				if (!method.HasBody || method.Body.Variables.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					for (int i = 0; i < method.Body.Variables.Count; i++) {
						var variable = method.Body.Variables[i];
						sb.AppendLine("  [" + i + "] " + variable.Type.FullName);
					}
				}

				sb.AppendLine();
				sb.AppendLine("CustomAttributes:");
				if (method.CustomAttributes.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					foreach (var attribute in method.CustomAttributes)
						sb.AppendLine("  " + FormatCustomAttribute(attribute));
				}

				return sb.ToString();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Get_Type_Hierarchy", MCPCmdDescription = "Returns the base type chain, implemented interfaces and derived types known in currently loaded assemblies for a target type.")]
		public static string GetTypeHierarchy(string Assembly, string Namespace, string ClassName, int MaxDerivedTypes = 50) {
			try {
				var typeInfo = EnumerateLoadedTypes(Assembly)
					.FirstOrDefault(info =>
						string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase));
				if (typeInfo == null)
					return "Type was not found.";

				var type = typeInfo.Type;
				var sb = new StringBuilder();
				sb.AppendLine("Assembly: " + typeInfo.AssemblyName);
				sb.AppendLine("Type: " + type.FullName);
				sb.AppendLine("Token: 0x" + type.MDToken.Raw.ToString("X8"));
				sb.AppendLine();
				sb.AppendLine("BaseChain:");
				var current = type.BaseType?.ResolveTypeDef();
				if (current == null) {
					sb.AppendLine("  <none>");
				}
				else {
					while (current != null) {
						sb.AppendLine("  " + current.FullName + " (0x" + current.MDToken.Raw.ToString("X8") + ")");
						current = current.BaseType?.ResolveTypeDef();
					}
				}

				sb.AppendLine();
				sb.AppendLine("Interfaces:");
				if (type.Interfaces.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					foreach (var iface in type.Interfaces.Select(i => i.Interface.FullName).Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
						sb.AppendLine("  " + iface);
				}

				sb.AppendLine();
				sb.AppendLine("DerivedTypes:");
				var maxDerivedTypes = NormalizeMaxResults(MaxDerivedTypes, 50, 500);
				var derived = EnumerateLoadedTypes()
					.Where(info => info.Type.BaseType != null && MetadataEquals(type, info.Type.BaseType))
					.OrderBy(info => info.Type.FullName, StringComparer.OrdinalIgnoreCase)
					.Take(maxDerivedTypes + 1)
					.ToList();
				if (derived.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					foreach (var entry in derived.Take(maxDerivedTypes))
						sb.AppendLine("  " + entry.AssemblyName + " :: " + entry.Type.FullName);
					if (derived.Count > maxDerivedTypes)
						sb.AppendLine("  ... truncated to " + maxDerivedTypes + " results");
				}

				return sb.ToString();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Find_Method_By_Token", MCPCmdDescription = "Finds a method definition by metadata token in a loaded assembly.")]
		public static string FindMethodByToken(string Assembly, string Token) {
			try {
				if (!TryParseMetadataToken(Token, out var token, out var error))
					return error;

				foreach (var methodInfo in EnumerateLoadedMethods(Assembly)) {
					if (methodInfo.Method.MDToken.Raw != token)
						continue;

					return FormatMethodIdentity(methodInfo.AssemblyName, methodInfo.Method) + Environment.NewLine +
						"Module: " + methodInfo.Module.Name;
				}

				return "No method with token 0x" + token.ToString("X8") + " was found in assembly " + Assembly + ".";
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Find_Type_By_Token", MCPCmdDescription = "Finds a type definition by metadata token in a loaded assembly.")]
		public static string FindTypeByToken(string Assembly, string Token) {
			try {
				if (!TryParseMetadataToken(Token, out var token, out var error))
					return error;

				foreach (var typeInfo in EnumerateLoadedTypes(Assembly)) {
					if (typeInfo.Type.MDToken.Raw != token)
						continue;

					return FormatTypeIdentity(typeInfo.AssemblyName, typeInfo.Type) + Environment.NewLine +
						"Module: " + typeInfo.Module.Name;
				}

				return "No type with token 0x" + token.ToString("X8") + " was found in assembly " + Assembly + ".";
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Find_Callers", MCPCmdDescription = "Finds loaded methods whose IL references a target method definition.")]
		[Command("Search_References_To_Method", MCPCmdDescription = "Finds loaded methods whose IL references a target method definition.")]
		public static string FindCallers(string Assembly, string Namespace, string ClassName, string MethodName, string MethodSignature = null, int MaxResults = 100) {
			try {
				var targetMethods = EnumerateLoadedMethods(Assembly)
					.Where(info =>
						string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Method.Name.String, MethodName, StringComparison.OrdinalIgnoreCase) &&
						MatchesMethodSignature(info.Method, MethodSignature))
					.Select(info => info.Method)
					.ToList();

				if (targetMethods.Count == 0)
					return "Target method was not found.";
				if (targetMethods.Count > 1)
					return "Multiple overloads matched: " + string.Join(" | ", targetMethods.Select(method => method.FullName));

				var targetMethod = targetMethods[0];
				var maxResults = NormalizeMaxResults(MaxResults, 100, 500);
				var hits = new List<string>();
				foreach (var methodInfo in EnumerateLoadedMethods(Assembly)) {
					if (!methodInfo.Method.HasBody)
						continue;

					foreach (var instruction in methodInfo.Method.Body.Instructions) {
						if (!InstructionReferencesMethod(instruction, targetMethod))
							continue;

						hits.Add(methodInfo.AssemblyName + " :: " + methodInfo.Type.FullName + " :: " + methodInfo.Method.FullName +
							" :: IL_0x" + instruction.Offset.ToString("X4") + " " + instruction.OpCode.Name);
						if (hits.Count > maxResults)
							break;
					}

					if (hits.Count > maxResults)
						break;
				}

				if (hits.Count == 0)
					return "No callers were found for " + targetMethod.FullName + ".";

				var sb = new StringBuilder();
				sb.AppendLine("Target: " + targetMethod.FullName);
				foreach (var hit in hits.Take(maxResults))
					sb.AppendLine(hit);
				if (hits.Count > maxResults)
					sb.AppendLine("... truncated to " + maxResults + " results");
				return sb.ToString();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Find_Callees", MCPCmdDescription = "Lists direct method references called from the target method body, including external member references.")]
		public static string FindCallees(string Assembly, string Namespace, string ClassName, string MethodName, string MethodSignature = null, int MaxResults = 100) {
			try {
				var matches = EnumerateLoadedMethods(Assembly)
					.Where(info =>
						string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Method.Name.String, MethodName, StringComparison.OrdinalIgnoreCase) &&
						MatchesMethodSignature(info.Method, MethodSignature))
					.ToList();

				if (matches.Count == 0)
					return "Target method was not found.";
				if (matches.Count > 1)
					return "Multiple overloads matched: " + string.Join(" | ", matches.Select(match => match.Method.FullName));

				var methodInfo = matches[0];
				if (!methodInfo.Method.HasBody)
					return "The target method has no body.";

				var maxResults = NormalizeMaxResults(MaxResults, 100, 500);
				var callees = new List<string>();
				foreach (var instruction in methodInfo.Method.Body.Instructions) {
					if (instruction.Operand is not IMethod methodOperand)
						continue;

					var resolved = methodOperand.ResolveMethodDef();
					var callee = resolved != null
						? resolved.FullName + " :: token=0x" + resolved.MDToken.Raw.ToString("X8")
						: methodOperand.FullName;
					callees.Add("IL_0x" + instruction.Offset.ToString("X4") + " " + instruction.OpCode.Name + " -> " + callee);
					if (callees.Count > maxResults)
						break;
				}

				if (callees.Count == 0)
					return "No direct callees were found in " + methodInfo.Method.FullName + ".";

				var sb = new StringBuilder();
				sb.AppendLine("Target: " + methodInfo.Method.FullName);
				foreach (var callee in callees.Take(maxResults))
					sb.AppendLine(callee);
				if (callees.Count > maxResults)
					sb.AppendLine("... truncated to " + maxResults + " results");
				return sb.ToString();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Search_References_To_Type", MCPCmdDescription = "Searches IL instructions that reference a target type directly or via member references.")]
		public static string SearchReferencesToType(string Assembly, string Namespace, string ClassName, int MaxResults = 100) {
			try {
				var targetType = EnumerateLoadedTypes(Assembly)
					.Where(info =>
						string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase))
					.Select(info => info.Type)
					.FirstOrDefault();
				if (targetType == null)
					return "Target type was not found.";

				var maxResults = NormalizeMaxResults(MaxResults, 100, 500);
				var hits = new List<string>();
				foreach (var methodInfo in EnumerateLoadedMethods(Assembly)) {
					if (!methodInfo.Method.HasBody)
						continue;

					foreach (var instruction in methodInfo.Method.Body.Instructions) {
						if (!InstructionReferencesType(instruction, targetType))
							continue;

						hits.Add(methodInfo.AssemblyName + " :: " + methodInfo.Type.FullName + " :: " + methodInfo.Method.Name +
							" :: IL_0x" + instruction.Offset.ToString("X4") + " " + instruction.OpCode.Name);
						if (hits.Count > maxResults)
							break;
					}

					if (hits.Count > maxResults)
						break;
				}

				if (hits.Count == 0)
					return "No references were found to type " + targetType.FullName + ".";

				var sb = new StringBuilder();
				sb.AppendLine("Target: " + targetType.FullName);
				foreach (var hit in hits.Take(maxResults))
					sb.AppendLine(hit);
				if (hits.Count > maxResults)
					sb.AppendLine("... truncated to " + maxResults + " results");
				return sb.ToString();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Search_Custom_Attributes", MCPCmdDescription = "Searches custom attributes across assembly/module/type/member/parameter metadata.")]
		public static string SearchCustomAttributes(string Assembly, string AttributeName, int MaxResults = 100) {
			try {
				if (string.IsNullOrWhiteSpace(AttributeName))
					return "AttributeName cannot be empty.";

				var maxResults = NormalizeMaxResults(MaxResults, 100, 500);
				var hits = EnumerateCustomAttributeHits(Assembly)
					.Where(hit => ContainsIgnoreCase(hit.Attribute.AttributeType?.FullName ?? string.Empty, AttributeName))
					.Take(maxResults + 1)
					.ToList();

				if (hits.Count == 0)
					return "No matching custom attributes were found.";

				var sb = new StringBuilder();
				foreach (var hit in hits.Take(maxResults))
					sb.AppendLine(hit.AssemblyName + " :: " + hit.TargetKind + " :: " + hit.TargetId + " :: " + FormatCustomAttribute(hit.Attribute));
				if (hits.Count > maxResults)
					sb.AppendLine("... truncated to " + maxResults + " results");
				return sb.ToString();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Get_Attribute_Details", MCPCmdDescription = "Returns detailed matches for a custom attribute including constructor and named arguments.")]
		public static string GetAttributeDetails(string Assembly, string AttributeName, string TargetFilter = null, int MaxResults = 25) {
			try {
				if (string.IsNullOrWhiteSpace(AttributeName))
					return "AttributeName cannot be empty.";

				var maxResults = NormalizeMaxResults(MaxResults, 25, 200);
				var hits = EnumerateCustomAttributeHits(Assembly)
					.Where(hit => ContainsIgnoreCase(hit.Attribute.AttributeType?.FullName ?? string.Empty, AttributeName))
					.Where(hit => string.IsNullOrWhiteSpace(TargetFilter) || ContainsIgnoreCase(hit.TargetId, TargetFilter))
					.Take(maxResults + 1)
					.ToList();

				if (hits.Count == 0)
					return "No matching attribute details were found.";

				var sb = new StringBuilder();
				foreach (var hit in hits.Take(maxResults)) {
					sb.AppendLine("Assembly: " + hit.AssemblyName);
					sb.AppendLine("TargetKind: " + hit.TargetKind);
					sb.AppendLine("Target: " + hit.TargetId);
					sb.AppendLine("AttributeType: " + (hit.Attribute.AttributeType?.FullName ?? "<unknown>"));
					sb.AppendLine("Attribute: " + FormatCustomAttribute(hit.Attribute));
					sb.AppendLine();
				}
				if (hits.Count > maxResults)
					sb.AppendLine("... truncated to " + maxResults + " results");
				return sb.ToString().TrimEnd();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Get_Resource_Names", MCPCmdDescription = "Lists manifest resources from a loaded assembly and shows their resource kind.")]
		public static string GetResourceNames(string Assembly) {
			try {
				var modules = GetModuleNodes(Assembly).ToList();
				if (modules.Count == 0)
					return "Assembly " + Assembly + " is not loaded.";

				var sb = new StringBuilder();
				foreach (var modNode in modules) {
					var module = modNode.GetModule();
					sb.AppendLine("Module: " + module.Name);
					if (module.Resources.Count == 0) {
						sb.AppendLine("  <none>");
						sb.AppendLine();
						continue;
					}

					foreach (var resource in module.Resources.OrderBy(resource => resource.Name?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
						sb.AppendLine("  " + resource.Name + " :: " + resource.ResourceType);
					sb.AppendLine();
				}

				return sb.ToString().TrimEnd();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Get_Embedded_Resource_Content", MCPCmdDescription = "Returns a safe preview of an embedded resource. Text resources are decoded when possible, otherwise a hex preview is returned.")]
		public static string GetEmbeddedResourceContent(string Assembly, string ResourceName, int MaxBytes = 4096) {
			try {
				foreach (var modNode in GetModuleNodes(Assembly)) {
					var module = modNode.GetModule();
					var resource = module.Resources.FirstOrDefault(candidate => string.Equals(candidate.Name, ResourceName, StringComparison.OrdinalIgnoreCase));
					if (resource == null)
						continue;

					if (resource is not EmbeddedResource embedded)
						return "Resource '" + ResourceName + "' exists but is not an EmbeddedResource. Type=" + resource.ResourceType;

					var bytes = embedded.CreateReader().ToArray();
					var maxBytes = Math.Max(1, Math.Min(MaxBytes, 1024 * 1024));
					var sb = new StringBuilder();
					sb.AppendLine("Assembly: " + (module.Assembly?.Name ?? module.Name));
					sb.AppendLine("Module: " + module.Name);
					sb.AppendLine("Resource: " + embedded.Name);
					sb.AppendLine("Attributes: " + embedded.Attributes);
					sb.AppendLine("Size: " + bytes.Length + " bytes");
					sb.AppendLine("PreviewBytes: " + Math.Min(bytes.Length, maxBytes));
					sb.AppendLine();
					sb.AppendLine(GetResourcePreview(embedded, maxBytes));
					return sb.ToString().TrimEnd();
				}

				return "Embedded resource '" + ResourceName + "' was not found in assembly " + Assembly + ".";
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Browse_Assembly_Map", MCPCmdDescription = "Returns a structured assembly -> namespace -> type -> method map with optional filters and truncation controls.")]
		public static string BrowseAssemblyMap(string Assembly, string NamespaceFilter = null, string TypeFilter = null, bool IncludeMethods = true, bool IncludeMethodSignatures = false, bool IncludeTokens = true, int MaxNamespaces = 200, int MaxTypesPerNamespace = 200, int MaxMethodsPerType = 100) {
			try {
				return BuildAssemblyMap(Assembly, NamespaceFilter, TypeFilter, IncludeMethods, IncludeMethodSignatures, IncludeTokens, MaxNamespaces, MaxTypesPerNamespace, MaxMethodsPerType);
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("List_Type_Members", MCPCmdDescription = "Lists fields, properties, events and methods of a target type in a compact structured format.")]
		public static string ListTypeMembers(string Assembly, string Namespace, string ClassName, bool IncludeMethodSignatures = true, bool IncludeTokens = true) {
			try {
				var typeInfo = EnumerateLoadedTypes(Assembly)
					.FirstOrDefault(info =>
						string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase));
				if (typeInfo == null)
					return "Type was not found.";

				var type = typeInfo.Type;
				var sb = new StringBuilder();
				sb.AppendLine("Assembly: " + typeInfo.AssemblyName);
				sb.AppendLine("Module: " + typeInfo.Module.Name);
				sb.AppendLine("Type: " + type.FullName);
				if (IncludeTokens)
					sb.AppendLine("Token: 0x" + type.MDToken.Raw.ToString("X8"));
				sb.AppendLine();

				sb.AppendLine("Fields:");
				if (type.Fields.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					foreach (var field in type.Fields.OrderBy(field => field.Name.String, StringComparer.OrdinalIgnoreCase)) {
						sb.Append("  ").Append(field.FieldType.FullName).Append(" ").Append(field.Name);
						if (IncludeTokens)
							sb.Append(" :: token=0x").Append(field.MDToken.Raw.ToString("X8"));
						sb.AppendLine();
					}
				}

				sb.AppendLine();
				sb.AppendLine("Properties:");
				if (type.Properties.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					foreach (var property in type.Properties.OrderBy(property => property.Name.String, StringComparer.OrdinalIgnoreCase)) {
						sb.Append("  ").Append(property.PropertySig.GetRetType().FullName).Append(" ").Append(property.Name);
						if (IncludeTokens)
							sb.Append(" :: token=0x").Append(property.MDToken.Raw.ToString("X8"));
						sb.AppendLine();
					}
				}

				sb.AppendLine();
				sb.AppendLine("Events:");
				if (type.Events.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					foreach (var evt in type.Events.OrderBy(evt => evt.Name.String, StringComparer.OrdinalIgnoreCase)) {
						sb.Append("  ").Append(evt.EventType.FullName).Append(" ").Append(evt.Name);
						if (IncludeTokens)
							sb.Append(" :: token=0x").Append(evt.MDToken.Raw.ToString("X8"));
						sb.AppendLine();
					}
				}

				sb.AppendLine();
				sb.AppendLine("Methods:");
				if (type.Methods.Count == 0) {
					sb.AppendLine("  <none>");
				}
				else {
					foreach (var method in type.Methods.OrderBy(method => method.Name.String, StringComparer.OrdinalIgnoreCase)) {
						sb.Append("  ").Append(method.Name);
						if (IncludeMethodSignatures)
							sb.Append(" :: ").Append(method.MethodSig != null ? method.MethodSig.ToString() : "<no-signature>");
						if (IncludeTokens)
							sb.Append(" :: token=0x").Append(method.MDToken.Raw.ToString("X8"));
						sb.AppendLine();
					}
				}

				return sb.ToString().TrimEnd();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dump.All", MCPCmdDescription = "Compatibility alias. Returns a structured assembly map instead of the old raw dump output.")]
		public static string DumpAllCompatibility(string NamespaceAssemblyName, bool IncludeMethods = true, bool IncludeMethodSignatures = true, bool IncludeTokens = true, int MaxNamespaces = 200, int MaxTypesPerNamespace = 200, int MaxMethodsPerType = 100) {
			try {
				return BuildAssemblyMap(NamespaceAssemblyName, includeMethods: IncludeMethods, includeMethodSignatures: IncludeMethodSignatures, includeTokens: IncludeTokens, maxNamespaces: MaxNamespaces, maxTypesPerNamespace: MaxTypesPerNamespace, maxMethodsPerType: MaxMethodsPerType);
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dump.Method.From.Class", MCPCmdDescription = "Compatibility alias. Lists members of a target type, and can optionally return decompiled source.")]
		public static string DumpMethodsFromClassCompatibility(string Assembly, string Namespace, string ClassName, bool DumpMethods = true, bool DumpCode = false, bool IncludeTokens = true) {
			try {
				if (DumpCode)
					return DumpClassCode(Assembly, Namespace, ClassName);

				if (!DumpMethods)
					return ListTypeMembers(Assembly, Namespace, ClassName, IncludeMethodSignatures: false, IncludeTokens: IncludeTokens);

				return ListTypeMembers(Assembly, Namespace, ClassName, IncludeMethodSignatures: true, IncludeTokens: IncludeTokens);
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dump.Specific.Method", MCPCmdDescription = "Compatibility alias. Lists or decompiles matching methods within a target type.")]
		public static string DumpSpecificMethodCompatibility(string Assembly, string Namespace, string ClassName, string MethodName, bool DumpCode = false, string MethodSignature = null) {
			try {
				if (DumpCode)
					return DumpMethodsSourcode(Assembly, Namespace, ClassName, MethodName);

				var matches = EnumerateLoadedMethods(Assembly)
					.Where(info =>
						string.Equals(info.Type.Namespace ?? string.Empty, Namespace ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Type.Name.String, ClassName, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(info.Method.Name.String, MethodName, StringComparison.OrdinalIgnoreCase) &&
						MatchesMethodSignature(info.Method, MethodSignature))
					.ToList();

				if (matches.Count == 0)
					return "No matching methods were found.";

				var sb = new StringBuilder();
				sb.AppendLine("Matches: " + matches.Count);
				foreach (var match in matches)
					sb.AppendLine(match.AssemblyName + " :: " + match.Method.FullName + " :: token=0x" + match.Method.MDToken.Raw.ToString("X8"));
				return sb.ToString().TrimEnd();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}
	}
}
