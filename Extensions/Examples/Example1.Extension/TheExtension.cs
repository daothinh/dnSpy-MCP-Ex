using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Text;
using dnlib.DotNet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Each extension should export one class implementing IExtension

namespace Example1.Extension {
	[ExportExtension]

	sealed class TheExtension : IExtension {
		public IEnumerable<string> MergedResourceDictionaries {
			get {
				yield break;
			}
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "dnSpy MCP server and reverse-engineering helpers.",
			Copyright = "AgentSmithers dnSpy MCP extension"
		};

		public static string DumpSource(ModuleDocumentNode Mod, ModuleDef moduleDef) {
			var decCtx = new DecompilationContext();
			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb)) {
				var indenter = new Indenter(4, 4, true);
				var textOutput = new TextWriterDecompilerOutput(sw, indenter);
				Mod.Context.Decompiler.Decompile(moduleDef, textOutput, decCtx);  // :contentReference[oaicite:1]{index=1}
			}
			try {
				Debug.WriteLine(sb.ToString());
				return sb.ToString();
			}
			catch (ExternalException) {
				// swallow
			}
			return null;
		}

		public static string DumpSource(ModuleDocumentNode Mod, MethodDef methodDef) { //MethodDef is a function
			var decCtx = new DecompilationContext();
			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb)) {
				var indenter = new Indenter(4, 4, true);
				var textOutput = new TextWriterDecompilerOutput(sw, indenter);
				Mod.Context.Decompiler.Decompile(methodDef, textOutput, decCtx);  // :contentReference[oaicite:1]{index=1}
			}
			try {
				Debug.WriteLine(sb.ToString());
				return sb.ToString();
			}
			catch (ExternalException) {
				// swallow
			}
			return null;
		}

		public static string DumpSource(ModuleDocumentNode Mod, TypeDef typeDef) { //typeDef is a class
			var decCtx = new DecompilationContext();
			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb)) {
				var indenter = new Indenter(4, 4, true);
				var textOutput = new TextWriterDecompilerOutput(sw, indenter);
				Mod.Context.Decompiler.Decompile(typeDef, textOutput, decCtx);  // :contentReference[oaicite:1]{index=1}
			}
			try {
				Debug.WriteLine(sb.ToString());
				//Clipboard.SetText(sb.ToString());
				return sb.ToString();
			}
			catch (ExternalException) {
				// swallow
			}
			return null;
		}

		public static string UpdateSource(ModuleDocumentNode modNode, MethodDef methodDef, string newCSharpBody) // just the statements inside the MethodDef (Function)
		{
			string source = "";
			try {
				// 1) Build a small C# source wrapper
				//    matching the signature of methodDef:
				var retType = methodDef.ReturnType.FullName;
				var parameters = string.Join(", ", methodDef.Parameters.Where(p => !p.IsHiddenThisParameter).Select(p => p.Type.FullName + " " + p.Name));
				source = $@"using System;
				public static class __Patch {{
					public static {retType} {methodDef.Name}({parameters}) {{
						{newCSharpBody}
					}}
				}}";

				/*
				using System;
				public static class __Patch {
					public static System.String HelloWorld(CNETTrafficFighterWeb.com.myqnapcloud.desertqnap.API ) {
						Console.WriteLine("Hello from patched method!"); return "TestedValue";
					}
				}
				*/

				// 2) Compile with Roslyn into a MemoryStream
				var tree = CSharpSyntaxTree.ParseText(source);
				var refs = new[]
				{
					MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
					MetadataReference.CreateFromFile(modNode.GetModule().Location)
				};
				var comp = CSharpCompilation.Create(
					"__PatchAsm",
					new[] { tree },
					refs,
					new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
				);
				using var ms = new MemoryStream();
				var result = comp.Emit(ms);
				if (!result.Success)
					return "❌ Compilation errors:\n" + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
					.Select(d => d.ToString()));

				// 3) Load compiled bytes with dnlib
				var patchMod = ModuleDefMD.Load(ms.ToArray());
				var patchType = patchMod.Types.First(t => t.Name == "__Patch");
				var patchMethod = patchType.Methods.First(m => m.Name == methodDef.Name);

				// 4) Copy its IL into your target
				var body = methodDef.Body;
				body.Instructions.Clear();
				foreach (var instr in patchMethod.Body.Instructions)
					body.Instructions.Add(instr);

				// 5) Refresh dnSpy’s UI
				Global.MyTreeView.TreeView.RefreshAllNodes();

				return $"✅ Updated method body of {methodDef.Name}";
			}
			catch (Exception) {
				return "Exception: Failed to update function\r\n\t\n" + source;
			}
		}

		public void OnEvent(ExtensionEvent @event, object obj) {
			if (@event == ExtensionEvent.AppExit)
				Global.MySimpleMCPServer?.Stop();
		}
	}
}
