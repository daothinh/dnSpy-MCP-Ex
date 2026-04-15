using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.Metadata;
using static Example1.Extension.SimpleMcpServer;

namespace Example1.Extension {
	partial class MCPCommands {
		static IEnumerable<DbgModule> EnumerateDebugModules(int processId = 0) {
			var processes = processId > 0
				? Global.MyDbgManager.Processes.Where(process => process.Id == processId).ToArray()
				: Global.MyDbgManager.Processes;

			return processes
				.SelectMany(process => process.Runtimes)
				.SelectMany(runtime => runtime.Modules);
		}

		static string FormatStackFrameDetailed(DbgStackFrame frame, int index, bool isActive = false) {
			var sb = new StringBuilder();
			sb.Append(isActive ? "*" : " ").Append(" #").Append(index);
			sb.Append(" Thread=").Append(frame.Thread.Id);
			sb.Append(" Token=").Append(frame.HasFunctionToken ? "0x" + frame.FunctionToken.ToString("X8") : "<none>");
			sb.Append(" ILOffset=0x").Append(frame.FunctionOffset.ToString("X"));
			sb.Append(" Flags=").Append(frame.Flags);
			if (frame.Module != null)
				sb.Append(" Module=").Append(frame.Module.Name);
			if (frame.Location is DbgDotNetCodeLocation location)
				sb.Append(" Location=token=0x").Append(location.Token.ToString("X8")).Append(" il=0x").Append(location.Offset.ToString("X"));
			return sb.ToString();
		}

		static bool TryResolveFrameMethod(DbgStackFrame frame, out ModuleDef metadataModule, out MethodDef method) {
			metadataModule = null;
			method = null;

			if (frame == null || frame.Module == null || !frame.HasFunctionToken || Global.MyDbgMetadataService == null)
				return false;

			metadataModule = Global.MyDbgMetadataService.TryGetMetadata(frame.Module, DbgLoadModuleOptions.AutoLoaded);
			if (metadataModule == null)
				return false;

			method = metadataModule.ResolveToken(frame.FunctionToken) as MethodDef;
			return method != null;
		}

		[Command("Dbg_Get_Break_Reason_Details", MCPCmdDescription = "Returns the current runtime break reasons with richer details, including exception payloads when available.")]
		public static string DbgGetBreakReasonDetails() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var runtime = Global.MyDbgManager.CurrentRuntime.Current;
					if (runtime == null)
						return "No current debug runtime.";
					if (runtime.BreakInfos.Count == 0)
						return "The current runtime has no break info entries.";

					var sb = new StringBuilder();
					sb.AppendLine("Runtime: " + runtime.Name);
					for (int i = 0; i < runtime.BreakInfos.Count; i++) {
						var info = runtime.BreakInfos[i];
						sb.AppendLine("[" + i + "] Kind=" + info.Kind + " Summary=" + SanitizeForSingleLine(info.ToString(), 240));
						if (info.Data is DbgMessageExceptionThrownEventArgs exceptionArgs) {
							sb.AppendLine(FormatCurrentException(exceptionArgs.Exception).TrimEnd());
						}
					}
					return sb.ToString().TrimEnd();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Active_Statement", MCPCmdDescription = "Returns the currently active statement, resolved back to metadata when possible.")]
		public static string DbgGetActiveStatement() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var callStackService = Global.MyDbgCallStackService;
					if (callStackService == null)
						return "Call stack service is not available.";

					var frame = callStackService.ActiveFrame;
					if (frame == null)
						return "There is no active stack frame.";

					var sb = new StringBuilder();
					sb.AppendLine(FormatStackFrameDetailed(frame, callStackService.ActiveFrameIndex, isActive: true));
					if (TryResolveFrameMethod(frame, out var metadataModule, out var method)) {
						sb.AppendLine("ResolvedModule: " + metadataModule.Name);
						sb.AppendLine("ResolvedType: " + method.DeclaringType.FullName);
						sb.AppendLine("ResolvedMethod: " + method.FullName);
					}
					return sb.ToString().TrimEnd();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Thread_Frames", MCPCmdDescription = "Returns call stack frames for a specific thread without changing dnSpy's active thread or frame selection.")]
		public static string DbgGetThreadFrames(ulong ThreadId, int ProcessId = 0, int MaxFrames = 20) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var thread = GetDebuggedThread(ThreadId, ProcessId);
					if (thread == null)
						return "Thread " + ThreadId + " was not found in the current debug session.";

					var maxFrames = NormalizeMaxResults(MaxFrames, 20, 200);
					var frames = thread.GetFrames(maxFrames);
					if (frames.Length == 0)
						return "No frames are available for thread " + ThreadId + ".";

					try {
						var activeFrame = Global.MyDbgCallStackService?.ActiveFrame;
						var sb = new StringBuilder();
						for (int i = 0; i < frames.Length; i++)
							sb.AppendLine(FormatStackFrameDetailed(frames[i], i, activeFrame == frames[i]));
						return sb.ToString().TrimEnd();
					}
					finally {
						CloseDebugObjects(frames);
					}
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Find_Module", MCPCmdDescription = "Searches loaded debug runtime modules by module name, file path or runtime name.")]
		public static string DbgFindModule(string SearchTerm, int ProcessId = 0, int MaxResults = 50) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;
				if (string.IsNullOrWhiteSpace(SearchTerm))
					return "SearchTerm cannot be empty.";

				return RunOnDebugDispatcher(() => {
					var maxResults = NormalizeMaxResults(MaxResults, 50, 500);
					var matches = EnumerateDebugModules(ProcessId)
						.Where(module =>
							ContainsIgnoreCase(module.Name, SearchTerm) ||
							ContainsIgnoreCase(module.Filename, SearchTerm) ||
							ContainsIgnoreCase(module.Runtime.Name, SearchTerm))
						.OrderBy(module => module.Process.Id)
						.ThenBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
						.Take(maxResults + 1)
						.ToList();

					if (matches.Count == 0)
						return "No runtime modules matched '" + SearchTerm + "'.";

					var sb = new StringBuilder();
					foreach (var module in matches.Take(maxResults))
						sb.AppendLine(FormatModule(module));
					if (matches.Count > maxResults)
						sb.AppendLine("... truncated to " + maxResults + " results");
					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Find_Type", MCPCmdDescription = "Searches metadata from loaded runtime modules for matching type names.")]
		public static string DbgFindType(string SearchTerm, int ProcessId = 0, int MaxResults = 50) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;
				if (Global.MyDbgMetadataService == null)
					return "Debugger metadata service is not available.";
				if (string.IsNullOrWhiteSpace(SearchTerm))
					return "SearchTerm cannot be empty.";

				return RunOnDebugDispatcher(() => {
					var maxResults = NormalizeMaxResults(MaxResults, 50, 500);
					var hits = new List<string>();
					foreach (var module in EnumerateDebugModules(ProcessId).OrderBy(module => module.Process.Id).ThenBy(module => module.Name, StringComparer.OrdinalIgnoreCase)) {
						var metadata = Global.MyDbgMetadataService.TryGetMetadata(module, DbgLoadModuleOptions.AutoLoaded);
						if (metadata == null)
							continue;

						foreach (var type in metadata.GetTypes()) {
							if (!ContainsIgnoreCase(type.FullName, SearchTerm) && !ContainsIgnoreCase(type.Name.String, SearchTerm))
								continue;

							hits.Add("PID=" + module.Process.Id + " :: " + module.Name + " :: " + type.FullName + " :: token=0x" + type.MDToken.Raw.ToString("X8"));
							if (hits.Count > maxResults)
								break;
						}

						if (hits.Count > maxResults)
							break;
					}

					if (hits.Count == 0)
						return "No runtime metadata types matched '" + SearchTerm + "'.";

					var sb = new StringBuilder();
					foreach (var hit in hits.Take(maxResults))
						sb.AppendLine(hit);
					if (hits.Count > maxResults)
						sb.AppendLine("... truncated to " + maxResults + " results");
					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Find_Method", MCPCmdDescription = "Searches metadata from loaded runtime modules for matching method names or signatures.")]
		public static string DbgFindMethod(string SearchTerm, int ProcessId = 0, int MaxResults = 50) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;
				if (Global.MyDbgMetadataService == null)
					return "Debugger metadata service is not available.";
				if (string.IsNullOrWhiteSpace(SearchTerm))
					return "SearchTerm cannot be empty.";

				return RunOnDebugDispatcher(() => {
					var maxResults = NormalizeMaxResults(MaxResults, 50, 500);
					var hits = new List<string>();
					foreach (var module in EnumerateDebugModules(ProcessId).OrderBy(module => module.Process.Id).ThenBy(module => module.Name, StringComparer.OrdinalIgnoreCase)) {
						var metadata = Global.MyDbgMetadataService.TryGetMetadata(module, DbgLoadModuleOptions.AutoLoaded);
						if (metadata == null)
							continue;

						foreach (var type in metadata.GetTypes()) {
							foreach (var method in type.Methods) {
								if (!ContainsIgnoreCase(method.Name.String, SearchTerm) && !ContainsIgnoreCase(method.FullName, SearchTerm))
									continue;

								hits.Add("PID=" + module.Process.Id + " :: " + module.Name + " :: " + method.FullName + " :: token=0x" + method.MDToken.Raw.ToString("X8"));
								if (hits.Count > maxResults)
									break;
							}

							if (hits.Count > maxResults)
								break;
						}

						if (hits.Count > maxResults)
							break;
					}

					if (hits.Count == 0)
						return "No runtime metadata methods matched '" + SearchTerm + "'.";

					var sb = new StringBuilder();
					foreach (var hit in hits.Take(maxResults))
						sb.AppendLine(hit);
					if (hits.Count > maxResults)
						sb.AppendLine("... truncated to " + maxResults + " results");
					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Evaluate_Batch", MCPCmdDescription = "Evaluates multiple expressions in the active frame and returns compact results in one round trip.")]
		public static string DbgEvaluateBatch(string[] Expressions, bool NoSideEffects = true, int MaxChildrenPerNode = 4) {
			try {
				var serviceError = EnsureDebuggerEvaluationServices();
				if (serviceError != null)
					return serviceError;
				if (Expressions == null || Expressions.Length == 0)
					return "Expressions cannot be empty.";

				return RunOnDebugDispatcher(() => {
					using (var activeEval = CreateActiveEvaluationContext()) {
						var maxChildren = NormalizeMaxResults(MaxChildrenPerNode, 4, 64);
						var allowFuncEval = !NoSideEffects;
						var sb = new StringBuilder();
						for (int i = 0; i < Expressions.Length; i++) {
							var expression = Expressions[i];
							if (string.IsNullOrWhiteSpace(expression)) {
								sb.AppendLine("[" + i + "] <empty>");
								continue;
							}

							var result = activeEval.Language.ValueNodeFactory.Create(
								activeEval.EvalInfo,
								expression,
								GetValueNodeOptions(hideCompilerGeneratedMembers: false, noFuncEval: NoSideEffects),
								GetExpressionEvaluationOptions(NoSideEffects),
								expressionEvaluatorState: null);

							try {
								sb.AppendLine("[" + i + "] " + expression);
								sb.AppendLine("  CausesSideEffects: " + result.CausesSideEffects);
								AppendValueNodeSummary(sb, activeEval.EvalInfo, result.ValueNode, "  Root: ", allowFuncEval);
								AppendValueNodeChildren(sb, activeEval.EvalInfo, result.ValueNode, maxChildren, allowFuncEval, noFuncEval: NoSideEffects, indentPrefix: "    ");
							}
							finally {
								CloseDebugObjects(new DbgObject[] { result.ValueNode });
							}
						}

						return sb.ToString().TrimEnd();
					}
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}
	}
}
