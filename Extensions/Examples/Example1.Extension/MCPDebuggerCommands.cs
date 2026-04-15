using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.DotNet.Metadata;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.Contracts.Metadata;
using static Example1.Extension.SimpleMcpServer;

namespace Example1.Extension {
	partial class MCPCommands {
		sealed class BreakpointTargetInfo {
			public string AssemblyName { get; set; }
			public string ModuleName { get; set; }
			public string TypeFullName { get; set; }
			public string MethodName { get; set; }
			public ModuleId ModuleId { get; set; }
			public Guid ModuleVersionId { get; set; }
			public uint MethodToken { get; set; }
			public uint ILOffset { get; set; }
		}

		sealed class ActiveEvaluationContext : IDisposable {
			public DbgLanguage Language { get; }
			public DbgStackFrame Frame { get; }
			public DbgEvaluationContext Context { get; }
			public DbgEvaluationInfo EvalInfo { get; }

			public ActiveEvaluationContext(DbgLanguage language, DbgStackFrame frame, DbgEvaluationContext context, CancellationToken cancellationToken) {
				Language = language ?? throw new ArgumentNullException(nameof(language));
				Frame = frame ?? throw new ArgumentNullException(nameof(frame));
				Context = context ?? throw new ArgumentNullException(nameof(context));
				EvalInfo = new DbgEvaluationInfo(context, frame, cancellationToken);
			}

			public void Dispose() => Context.Close();
		}

		const DbgValueNodeEvaluationOptions DefaultValueNodeOptions =
			DbgValueNodeEvaluationOptions.RespectHideMemberAttributes |
			DbgValueNodeEvaluationOptions.HideCompilerGeneratedMembers |
			DbgValueNodeEvaluationOptions.NoHideRoots;
		const DbgValueFormatterOptions DefaultValueFormatterOptions =
			DbgValueFormatterOptions.Decimal |
			DbgValueFormatterOptions.DigitSeparators |
			DbgValueFormatterOptions.Namespaces |
			DbgValueFormatterOptions.IntrinsicTypeKeywords;
		const DbgValueFormatterTypeOptions DefaultValueTypeFormatterOptions =
			DbgValueFormatterTypeOptions.Namespaces |
			DbgValueFormatterTypeOptions.IntrinsicTypeKeywords;

		static T RunOnUIThread<T>(Func<T> callback) {
			if (callback == null)
				throw new ArgumentNullException(nameof(callback));

			var dispatcher = Global.MyAppWindow?.MainWindow?.Dispatcher;
			if (dispatcher == null || dispatcher.CheckAccess())
				return callback();

			T result = default(T);
			Exception error = null;
			var done = new ManualResetEventSlim(false);
			int state = 0;
			dispatcher.BeginInvoke(new Action(() => {
				if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
					return;
				try {
					result = callback();
				}
				catch (Exception ex) {
					error = ex;
				}
				finally {
					done.Set();
				}
			}));

			try {
				if (!done.Wait(TimeSpan.FromSeconds(15))) {
					if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
						throw new TimeoutException("Timed out waiting for dnSpy UI dispatcher.");
					done.Wait();
				}
			}
			finally {
				done.Dispose();
			}

			if (error != null)
				throw error;
			return result;
		}

		static T RunOnDebugDispatcher<T>(Func<T> callback) {
			if (callback == null)
				throw new ArgumentNullException(nameof(callback));

			var dbgManager = Global.MyDbgManager ?? throw new InvalidOperationException("Debugger manager service is not available.");
			if (dbgManager.Dispatcher.CheckAccess())
				return callback();

			T result = default(T);
			Exception error = null;
			var done = new ManualResetEventSlim(false);
			int state = 0;
			dbgManager.Dispatcher.BeginInvoke(() => {
				if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
					return;
				try {
					result = callback();
				}
				catch (Exception ex) {
					error = ex;
				}
				finally {
					done.Set();
				}
			});

			try {
				if (!done.Wait(TimeSpan.FromSeconds(15))) {
					if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
						throw new TimeoutException("Timed out waiting for dnSpy debugger dispatcher.");
					done.Wait();
				}
			}
			finally {
				done.Dispose();
			}

			if (error != null)
				throw error;
			return result;
		}

		static T RunTimedTask<T>(Func<CancellationToken, Task<T>> factory, string operationName) {
			if (factory == null)
				throw new ArgumentNullException(nameof(factory));

			using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15))) {
				var task = factory(cts.Token);
				if (!task.Wait(TimeSpan.FromSeconds(16)))
					throw new TimeoutException("Timed out while waiting for " + operationName + ".");
				return task.GetAwaiter().GetResult();
			}
		}

		static bool ModuleMetadataMatches(BreakpointTargetInfo target, DbgModule module) {
			var metadataService = Global.MyDbgMetadataService;
			if (metadataService == null || target.ModuleVersionId == Guid.Empty)
				return true;

			var runtimeModule = metadataService.TryGetMetadata(module);
			if (runtimeModule == null || runtimeModule.Mvid == Guid.Empty)
				return true;

			return runtimeModule.Mvid == target.ModuleVersionId;
		}

		static string FormatThreadState(DbgThread thread) {
			if (thread == null || thread.State == null || thread.State.Count == 0)
				return "<none>";
			return string.Join(", ", thread.State.Select(a => a.ToString()));
		}

		static string GetBreakpointLocationSummary(DbgCodeBreakpoint breakpoint) {
			if (breakpoint == null)
				return "<null>";

			var dotNetLocation = breakpoint.Location as DbgDotNetCodeLocation;
			if (dotNetLocation != null) {
				var moduleName = dotNetLocation.DbgModule != null ? dotNetLocation.DbgModule.Name : Path.GetFileName(dotNetLocation.Module.ToString());
				return moduleName + " token=0x" + dotNetLocation.Token.ToString("X8") + " il=0x" + dotNetLocation.Offset.ToString("X");
			}

			return breakpoint.Location != null ? breakpoint.Location.Type : "<unknown>";
		}

		static string FormatBreakpoint(DbgCodeBreakpoint breakpoint) {
			var boundSummary = breakpoint.BoundBreakpointsMessage;
			var sb = new StringBuilder();
			sb.Append("Id=").Append(breakpoint.Id);
			sb.Append(" Enabled=").Append(breakpoint.IsEnabled);
			sb.Append(" Hidden=").Append(breakpoint.IsHidden);
			sb.Append(" OneShot=").Append(breakpoint.IsOneShot);
			sb.Append(" Location=").Append(GetBreakpointLocationSummary(breakpoint));
			sb.Append(" BoundCount=").Append(breakpoint.BoundBreakpoints.Length);
			if (boundSummary.Severity != DbgBoundCodeBreakpointSeverity.None || !string.IsNullOrWhiteSpace(boundSummary.Message))
				sb.Append(" BoundMessage=").Append(SanitizeForSingleLine(boundSummary.Message, 160));
			return sb.ToString();
		}

		static DbgProcess GetDebuggedProcess(int processId = 0) {
			var dbgManager = Global.MyDbgManager ?? throw new InvalidOperationException("Debugger manager service is not available.");
			if (!dbgManager.IsDebugging || dbgManager.Processes.Length == 0)
				return null;

			if (processId > 0)
				return dbgManager.Processes.FirstOrDefault(p => p.Id == processId);

			return dbgManager.CurrentProcess.Current ?? dbgManager.Processes.OrderBy(p => p.Id).FirstOrDefault();
		}

		static DbgThread GetDebuggedThread(ulong threadId, int processId = 0) {
			var process = GetDebuggedProcess(processId);
			if (process == null)
				return null;
			return process.Threads.FirstOrDefault(t => t.Id == threadId);
		}

		static string FormatModule(DbgModule module) {
			var sb = new StringBuilder();
			sb.Append("Process=").Append(module.Process.Id);
			sb.Append(" Runtime=").Append(SanitizeForSingleLine(module.Runtime.Name, 80));
			sb.Append(" Name=").Append(module.Name);
			sb.Append(" Dynamic=").Append(module.IsDynamic);
			sb.Append(" InMemory=").Append(module.IsInMemory);
			sb.Append(" Optimized=").Append(module.IsOptimized.HasValue ? module.IsOptimized.Value.ToString() : "<native>");
			sb.Append(" Order=").Append(module.Order);
			if (module.HasAddress)
				sb.Append(" Address=0x").Append(module.Address.ToString("X")).Append(" Size=0x").Append(module.Size.ToString("X"));
			if (!string.IsNullOrWhiteSpace(module.Filename))
				sb.Append(" File=").Append(SanitizeForSingleLine(module.Filename, 180));
			return sb.ToString();
		}

		static string FormatCurrentException(DbgException exception) {
			if (exception == null)
				return "No current exception information is available.";

			var sb = new StringBuilder();
			sb.AppendLine("Category: " + exception.Id.Category);
			sb.AppendLine("Id: " + exception.Id.ToString());
			sb.AppendLine("FirstChance: " + exception.IsFirstChance);
			sb.AppendLine("SecondChance: " + exception.IsSecondChance);
			sb.AppendLine("Unhandled: " + exception.IsUnhandled);
			sb.AppendLine("Message: " + (string.IsNullOrWhiteSpace(exception.Message) ? "<none>" : exception.Message));
			sb.AppendLine("HResult: " + (exception.HResult.HasValue ? "0x" + exception.HResult.Value.ToString("X8") : "<none>"));
			sb.AppendLine("Thread: " + (exception.Thread != null ? exception.Thread.Id.ToString() : "<none>"));
			sb.AppendLine("Module: " + (exception.Module != null ? exception.Module.Name : "<none>"));
			return sb.ToString();
		}

		static string FormatMemoryBlock(ulong address, byte[] bytes) {
			if (bytes == null || bytes.Length == 0)
				return "<empty>";

			var sb = new StringBuilder();
			for (int i = 0; i < bytes.Length; i += 16) {
				var chunk = bytes.Skip(i).Take(16).ToArray();
				sb.Append("0x").Append((address + (ulong)i).ToString("X16")).Append(": ");
				sb.Append(string.Join(" ", chunk.Select(b => b.ToString("X2"))).PadRight(16 * 3 - 1));
				sb.Append("  ");
				foreach (var b in chunk)
					sb.Append(b >= 32 && b <= 126 ? (char)b : '.');
				sb.AppendLine();
			}
			return sb.ToString();
		}

		static void CloseDebugObjects(IEnumerable<DbgObject> objects) {
			var dbgManager = Global.MyDbgManager;
			if (dbgManager == null || objects == null)
				return;

			var toClose = objects
				.Where(obj => obj != null && !obj.IsClosed)
				.Distinct()
				.ToArray();
			if (toClose.Length != 0)
				dbgManager.Close(toClose);
		}

		static string EnsureDebuggerEvaluationServices() {
			var serviceError = EnsureDebuggerServices();
			if (serviceError != null)
				return serviceError;
			if (Global.MyDbgLanguageService == null)
				return "Debugger evaluation services are not available in this dnSpy build.";
			if (Global.MyDbgCallStackService == null)
				return "Call stack service is not available.";
			return null;
		}

		static string EnsureDebuggerServices() {
			if (Global.MyDbgManager == null)
				return "Debugger services are not available in this dnSpy build.";
			return null;
		}

		static DbgLanguage GetCurrentLanguage(DbgRuntime runtime) {
			if (runtime == null)
				throw new ArgumentNullException(nameof(runtime));

			var languageService = Global.MyDbgLanguageService ?? throw new InvalidOperationException("Debugger language service is not available.");
			var language = languageService.GetCurrentLanguage(runtime.RuntimeKindGuid);
			if (language == null)
				throw new InvalidOperationException("dnSpy did not provide a debugger language for runtime '" + runtime.Name + "'.");
			return language;
		}

		static ActiveEvaluationContext CreateActiveEvaluationContext(DbgEvaluationContextOptions contextOptions = DbgEvaluationContextOptions.None) {
			var callStackService = Global.MyDbgCallStackService ?? throw new InvalidOperationException("Call stack service is not available.");
			var frame = callStackService.ActiveFrame;
			if (frame == null)
				throw new InvalidOperationException("The debugger is not currently paused on a stack frame.");
			if (frame.IsClosed)
				throw new InvalidOperationException("The active stack frame is no longer available.");

			var cancellationToken = CancellationToken.None;
			var language = GetCurrentLanguage(frame.Runtime);
			var context = language.CreateContext(frame, options: contextOptions, cancellationToken: cancellationToken);
			return new ActiveEvaluationContext(language, frame, context, cancellationToken);
		}

		static DbgValueNodeEvaluationOptions GetValueNodeOptions(bool hideCompilerGeneratedMembers = true, bool noFuncEval = true) {
			var settings = Global.MyDebuggerSettings;
			var options = DbgValueNodeEvaluationOptions.NoHideRoots;
			if (settings == null || settings.RespectHideMemberAttributes)
				options |= DbgValueNodeEvaluationOptions.RespectHideMemberAttributes;
			if (settings != null && settings.HideDeprecatedError)
				options |= DbgValueNodeEvaluationOptions.HideDeprecatedError;
			if (settings != null && settings.ShowOnlyPublicMembers)
				options |= DbgValueNodeEvaluationOptions.PublicMembers;

			var effectiveHideCompilerGeneratedMembers = hideCompilerGeneratedMembers && (settings == null || settings.HideCompilerGeneratedMembers);
			if (effectiveHideCompilerGeneratedMembers)
				options |= DbgValueNodeEvaluationOptions.HideCompilerGeneratedMembers;

			var effectiveNoFuncEval = noFuncEval || (settings != null && !settings.PropertyEvalAndFunctionCalls);
			if (effectiveNoFuncEval)
				options |= DbgValueNodeEvaluationOptions.NoFuncEval;
			return options;
		}

		static DbgLocalsValueNodeEvaluationOptions GetLocalsProviderOptions(bool showCompilerGenerated, bool showDecompilerGenerated, bool showRawLocals) {
			var options = DbgLocalsValueNodeEvaluationOptions.None;
			if (showCompilerGenerated)
				options |= DbgLocalsValueNodeEvaluationOptions.ShowCompilerGeneratedVariables;
			if (showDecompilerGenerated)
				options |= DbgLocalsValueNodeEvaluationOptions.ShowDecompilerGeneratedVariables;
			if (showRawLocals)
				options |= DbgLocalsValueNodeEvaluationOptions.ShowRawLocals;
			return options;
		}

		static DbgEvaluationOptions GetExpressionEvaluationOptions(bool noSideEffects, bool rawLocals = false) {
			var options = DbgEvaluationOptions.Expression;
			if (noSideEffects)
				options |= DbgEvaluationOptions.NoSideEffects | DbgEvaluationOptions.NoFuncEval;
			if (rawLocals)
				options |= DbgEvaluationOptions.RawLocals;
			return options;
		}

		static DbgValueFormatterOptions GetValueFormatterOptions(bool allowFuncEval) {
			var settings = Global.MyDebuggerSettings;
			var options = DbgValueFormatterOptions.Namespaces | DbgValueFormatterOptions.IntrinsicTypeKeywords;
			if (settings == null || !settings.UseHexadecimal)
				options |= DbgValueFormatterOptions.Decimal;
			if (settings == null || settings.UseDigitSeparators)
				options |= DbgValueFormatterOptions.DigitSeparators;
			if (settings != null && settings.FullString)
				options |= DbgValueFormatterOptions.FullString;

			var canFuncEval = allowFuncEval && (settings == null || settings.PropertyEvalAndFunctionCalls);
			if (canFuncEval) {
				options |= DbgValueFormatterOptions.FuncEval;
				if (settings == null || settings.UseStringConversionFunction)
					options |= DbgValueFormatterOptions.ToString;
			}
			else {
				options |= DbgValueFormatterOptions.NoDebuggerDisplay;
			}
			return options;
		}

		static DbgValueFormatterTypeOptions GetValueTypeFormatterOptions() {
			var settings = Global.MyDebuggerSettings;
			var options = DbgValueFormatterTypeOptions.Namespaces | DbgValueFormatterTypeOptions.IntrinsicTypeKeywords;
			if (settings == null || !settings.UseHexadecimal)
				options |= DbgValueFormatterTypeOptions.Decimal;
			if (settings == null || settings.UseDigitSeparators)
				options |= DbgValueFormatterTypeOptions.DigitSeparators;
			return options;
		}

		static string FormatValueNodeName(DbgEvaluationInfo evalInfo, DbgValueNode node, bool allowFuncEval) {
			var output = new DbgStringBuilderTextWriter();
			node.FormatName(evalInfo, output, GetValueFormatterOptions(allowFuncEval), cultureInfo: null);
			return output.Text;
		}

		static string FormatValueNodeValue(DbgEvaluationInfo evalInfo, DbgValueNode node, bool allowFuncEval) {
			var output = new DbgStringBuilderTextWriter();
			node.FormatValue(evalInfo, output, GetValueFormatterOptions(allowFuncEval), cultureInfo: null);
			return output.Text;
		}

		static string FormatValueNodeExpectedType(DbgEvaluationInfo evalInfo, DbgValueNode node, bool allowFuncEval) {
			var output = new DbgStringBuilderTextWriter();
			node.FormatExpectedType(evalInfo, output, GetValueTypeFormatterOptions(), GetValueFormatterOptions(allowFuncEval), cultureInfo: null);
			return output.Text;
		}

		static string FormatValueNodeActualType(DbgEvaluationInfo evalInfo, DbgValueNode node, bool allowFuncEval) {
			var output = new DbgStringBuilderTextWriter();
			node.FormatActualType(evalInfo, output, GetValueTypeFormatterOptions(), GetValueFormatterOptions(allowFuncEval), cultureInfo: null);
			return output.Text;
		}

		static ulong? TryGetChildCount(DbgEvaluationInfo evalInfo, DbgValueNode node) {
			if (node == null)
				return null;
			if (node.HasChildren == false)
				return 0;

			try {
				return node.GetChildCount(evalInfo);
			}
			catch {
				return null;
			}
		}

		static void AppendValueNodeSummary(StringBuilder sb, DbgEvaluationInfo evalInfo, DbgValueNode node, string prefix, bool allowFuncEval, string kind = null) {
			if (sb == null)
				throw new ArgumentNullException(nameof(sb));
			if (node == null)
				throw new ArgumentNullException(nameof(node));

			var name = SanitizeForSingleLine(FormatValueNodeName(evalInfo, node, allowFuncEval), 160);
			var value = SanitizeForSingleLine(FormatValueNodeValue(evalInfo, node, allowFuncEval), 220);
			var actualType = SanitizeForSingleLine(FormatValueNodeActualType(evalInfo, node, allowFuncEval), 160);
			var expectedType = SanitizeForSingleLine(FormatValueNodeExpectedType(evalInfo, node, allowFuncEval), 160);
			var expression = node.CanEvaluateExpression ? SanitizeForSingleLine(node.Expression, 180) : string.Empty;
			var childCount = TryGetChildCount(evalInfo, node);

			sb.Append(prefix);
			if (!string.IsNullOrWhiteSpace(kind))
				sb.Append("Kind=").Append(kind).Append(" ");
			sb.Append("Name=").Append(string.IsNullOrWhiteSpace(name) ? "<anonymous>" : name);
			sb.Append(" Value=").Append(string.IsNullOrWhiteSpace(value) ? "<empty>" : value);
			if (!string.IsNullOrWhiteSpace(actualType))
				sb.Append(" Type=").Append(actualType);
			if (!string.IsNullOrWhiteSpace(expectedType) && !string.Equals(expectedType, actualType, StringComparison.Ordinal))
				sb.Append(" DeclaredType=").Append(expectedType);
			if (!string.IsNullOrWhiteSpace(expression))
				sb.Append(" Expr=").Append(expression);
			if (node.HasError)
				sb.Append(" Error=").Append(SanitizeForSingleLine(node.ErrorMessage, 220));
			sb.Append(" ReadOnly=").Append(node.IsReadOnly);
			sb.Append(" SideEffects=").Append(node.CausesSideEffects);
			sb.Append(" HasChildren=").Append(node.HasChildren.HasValue ? node.HasChildren.Value.ToString() : "<unknown>");
			if (childCount.HasValue)
				sb.Append(" ChildCount=").Append(childCount.Value);
			sb.AppendLine();
		}

		static void AppendValueNodeChildren(StringBuilder sb, DbgEvaluationInfo evalInfo, DbgValueNode node, int maxChildren, bool allowFuncEval, bool noFuncEval, string indentPrefix) {
			if (sb == null)
				throw new ArgumentNullException(nameof(sb));
			if (node == null)
				throw new ArgumentNullException(nameof(node));
			if (maxChildren <= 0 || node.HasChildren == false)
				return;

			DbgValueNode[] children = null;
			try {
				var requestedChildren = NormalizeMaxResults(maxChildren, 8, 128);
				children = node.GetChildren(evalInfo, 0, requestedChildren, GetValueNodeOptions(hideCompilerGeneratedMembers: false, noFuncEval: noFuncEval));
				var totalChildren = TryGetChildCount(evalInfo, node);
				for (int i = 0; i < children.Length; i++)
					AppendValueNodeSummary(sb, evalInfo, children[i], indentPrefix + "[" + i + "] ", allowFuncEval);
				if (children.Length == 0)
					sb.Append(indentPrefix).AppendLine("<no children>");
				if (totalChildren.HasValue && totalChildren.Value > (ulong)children.Length)
					sb.Append(indentPrefix).AppendLine("... children truncated to " + children.Length + " of " + totalChildren.Value);
			}
			catch (Exception ex) {
				sb.Append(indentPrefix).AppendLine("ChildrenError: " + SanitizeForSingleLine(ex.Message, 220));
			}
			finally {
				if (children != null)
					CloseDebugObjects(children);
			}
		}

		static BreakpointTargetInfo ResolveBreakpointTarget(string assemblyName, string @namespace, string className, string methodName, string methodSignature, int ilOffset) {
			if (string.IsNullOrWhiteSpace(assemblyName))
				throw new ArgumentException("Assembly cannot be empty.", nameof(assemblyName));
			if (string.IsNullOrWhiteSpace(className))
				throw new ArgumentException("ClassName cannot be empty.", nameof(className));
			if (string.IsNullOrWhiteSpace(methodName))
				throw new ArgumentException("MethodName cannot be empty.", nameof(methodName));
			if (ilOffset < 0)
				throw new ArgumentOutOfRangeException(nameof(ilOffset), "ILOffset must be zero or greater.");

			return RunOnUIThread(() => {
				foreach (var modNode in GetModuleNodes(assemblyName)) {
					var module = modNode.GetModule();
					var type = FindType(module, @namespace, className);
					if (type == null)
						continue;

					var methods = type.Methods
						.Where(m => string.Equals(m.Name.String, methodName, StringComparison.OrdinalIgnoreCase))
						.ToList();
					if (!string.IsNullOrWhiteSpace(methodSignature)) {
						methods = methods
							.Where(m => ContainsIgnoreCase(m.FullName, methodSignature) || ContainsIgnoreCase(m.MethodSig.ToString(), methodSignature))
							.ToList();
					}
					if (methods.Count == 0)
						continue;
					if (methods.Count > 1) {
						var matches = string.Join(", ", methods.Select(m => m.FullName));
						throw new InvalidOperationException("Multiple overloads matched: " + matches + ". Pass MethodSignature to disambiguate.");
					}

					var method = methods[0];
					if (!method.HasBody || method.Body == null)
						throw new InvalidOperationException("Method " + method.FullName + " has no IL body.");

					var requestedOffset = (uint)ilOffset;
					var availableOffsets = method.Body.Instructions.Select(instr => (uint)instr.Offset).ToList();
					if (!availableOffsets.Contains(requestedOffset)) {
						var available = string.Join(", ", availableOffsets.Take(12).Select(a => "0x" + a.ToString("X")));
						throw new InvalidOperationException("IL offset 0x" + requestedOffset.ToString("X") + " was not found. Available offsets: " + available);
					}

					return new BreakpointTargetInfo {
						AssemblyName = module.Assembly != null ? module.Assembly.Name : module.Name,
						ModuleName = module.Name,
						TypeFullName = type.FullName,
						MethodName = method.Name,
						ModuleId = string.IsNullOrWhiteSpace(module.Location) ? ModuleId.CreateInMemory(module) : ModuleId.CreateFromFile(module),
						ModuleVersionId = module.Mvid ?? Guid.Empty,
						MethodToken = (uint)method.MDToken.Raw,
						ILOffset = requestedOffset,
					};
				}

				throw new InvalidOperationException("Method " + (@namespace ?? string.Empty) + "." + className + "." + methodName + " was not found in assembly " + assemblyName + ".");
			});
		}

		static DbgModule FindDebuggerModule(BreakpointTargetInfo target) {
			var dbgManager = Global.MyDbgManager;
			var moduleIdProvider = Global.MyDbgModuleIdProvider;
			if (dbgManager == null || moduleIdProvider == null)
				return null;

			var modules = dbgManager.Processes
				.SelectMany(process => process.Runtimes)
				.SelectMany(runtime => runtime.Modules)
				.ToList();

			var currentRuntime = dbgManager.CurrentRuntime.Current;
			if (currentRuntime != null) {
				var preferredMatches = currentRuntime.Modules
					.Where(module => {
						var moduleId = moduleIdProvider.GetModuleId(module);
						return moduleId.HasValue && moduleId.Value.Equals(target.ModuleId) && ModuleMetadataMatches(target, module);
					})
					.ToList();
				if (preferredMatches.Count == 1)
					return preferredMatches[0];
				if (preferredMatches.Count > 1)
					throw new InvalidOperationException("Multiple modules in the current runtime matched " + target.ModuleName + ". Refusing to guess.");
			}

			var matches = modules
				.Where(module => {
					var moduleId = moduleIdProvider.GetModuleId(module);
					return moduleId.HasValue && moduleId.Value.Equals(target.ModuleId) && ModuleMetadataMatches(target, module);
				})
				.ToList();

			if (matches.Count == 1)
				return matches[0];
			if (matches.Count > 1) {
				var processIds = string.Join(", ", matches.Select(module => module.Process.Id).Distinct().OrderBy(id => id));
				throw new InvalidOperationException("Multiple debugged processes matched module " + target.ModuleName + " (PIDs: " + processIds + "). Set focus to the correct process and try again.");
			}

			return null;
		}

		static string ExecuteStep(DbgStepKind stepKind, string commandName) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var dbgManager = Global.MyDbgManager;
					if (!dbgManager.IsDebugging)
						return "No active debug session.";

					var thread = dbgManager.CurrentThread.Current;
					if (thread == null)
						return "There is no current debug thread.";

					var stepper = thread.CreateStepper();
					try {
						if (!stepper.CanStep)
							return commandName + " is not available while the current process is running.";

						stepper.Step(stepKind, autoClose: true);
						return commandName + " requested on thread " + thread.Id + ".";
					}
					catch {
						stepper.Close();
						throw;
					}
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Status", MCPCmdDescription = "Returns debugger session state, current process/thread and latest break info.")]
		public static string DbgGetStatus() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var dbgManager = Global.MyDbgManager;
					var sb = new StringBuilder();
					sb.AppendLine("IsDebugging: " + dbgManager.IsDebugging);
					sb.AppendLine("IsRunning: " + (dbgManager.IsRunning.HasValue ? dbgManager.IsRunning.Value.ToString() : "<mixed>"));
					sb.AppendLine("CanRestart: " + dbgManager.CanRestart);
					sb.AppendLine("CanDetachWithoutTerminating: " + dbgManager.CanDetachWithoutTerminating);
					sb.AppendLine("Processes: " + dbgManager.Processes.Length);

					var currentProcess = dbgManager.CurrentProcess.Current;
					if (currentProcess != null)
						sb.AppendLine("CurrentProcess: " + currentProcess.Id + " " + (string.IsNullOrWhiteSpace(currentProcess.Name) ? Path.GetFileName(currentProcess.Filename) : currentProcess.Name));
					else
						sb.AppendLine("CurrentProcess: <none>");

					var currentThread = dbgManager.CurrentThread.Current;
					if (currentThread != null)
						sb.AppendLine("CurrentThread: " + currentThread.Id + " (" + currentThread.UIName + ")");
					else
						sb.AppendLine("CurrentThread: <none>");

					var currentRuntime = dbgManager.CurrentRuntime.Current;
					if (currentRuntime != null) {
						sb.AppendLine("CurrentRuntime: " + currentRuntime.Name);
						if (currentRuntime.BreakInfos.Count == 0)
							sb.AppendLine("BreakReason: <none>");
						else
							sb.AppendLine("BreakReason: " + string.Join(" | ", currentRuntime.BreakInfos.Select(info => info.ToString())));
					}
					else {
						sb.AppendLine("CurrentRuntime: <none>");
					}

					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Session_Snapshot", MCPCmdDescription = "Returns a compact snapshot of the current debug session including status, processes, modules, threads, call stack and breakpoints.")]
		public static string DbgGetSessionSnapshot(int MaxFrames = 8) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var sb = new StringBuilder();
					sb.AppendLine("[Status]");
					sb.AppendLine(DbgGetStatus().TrimEnd());
					sb.AppendLine();

					sb.AppendLine("[Processes]");
					sb.AppendLine(DbgListProcesses().TrimEnd());
					sb.AppendLine();

					sb.AppendLine("[Threads]");
					sb.AppendLine(DbgListThreads().TrimEnd());
					sb.AppendLine();

					sb.AppendLine("[Modules]");
					sb.AppendLine(DbgListModules().TrimEnd());
					sb.AppendLine();

					sb.AppendLine("[CallStack]");
					sb.AppendLine(DbgGetCallStack(MaxFrames).TrimEnd());
					sb.AppendLine();

					sb.AppendLine("[Breakpoints]");
					sb.AppendLine(DbgListBreakpoints().TrimEnd());
					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_List_Attachable_Processes", MCPCmdDescription = "Lists attachable managed processes that dnSpy can debug.")]
		public static string DbgListAttachableProcesses(string Filter = null, int MaxResults = 50) {
			try {
				var service = Global.MyAttachableProcessesService;
				if (service == null)
					return "Attachable process service is not available.";

				var maxResults = NormalizeMaxResults(MaxResults, 50, 500);
				var processes = RunTimedTask(
					ct => string.IsNullOrWhiteSpace(Filter) ? service.GetAttachableProcessesAsync(ct) : service.GetAttachableProcessesAsync(Filter, ct),
					"attachable process enumeration");

				var ordered = processes
					.OrderBy(p => p.ProcessId)
					.ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
					.Take(maxResults + 1)
					.ToList();

				if (ordered.Count == 0)
					return "No attachable processes were found.";

				var sb = new StringBuilder();
				foreach (var process in ordered.Take(maxResults)) {
					sb.Append(process.ProcessId)
						.Append(" | ")
						.Append(string.IsNullOrWhiteSpace(process.Name) ? "<unknown>" : process.Name)
						.Append(" | ")
						.Append(process.RuntimeName)
						.Append(" | ")
						.Append(process.Architecture)
						.Append(" | ")
						.Append(SanitizeForSingleLine(process.Filename, 180))
						.AppendLine();
				}

				if (ordered.Count > maxResults)
					sb.AppendLine("... truncated to " + maxResults + " results");

				return sb.ToString();
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Attach_Process", MCPCmdDescription = "Attaches dnSpy debugger to a process by PID.")]
		public static string DbgAttachProcess(int ProcessId) {
			try {
				var service = Global.MyAttachableProcessesService;
				if (service == null)
					return "Attachable process service is not available.";
				if (ProcessId <= 0)
					return "ProcessId must be greater than zero.";

				var attachable = RunTimedTask(
					ct => service.GetAttachableProcessesAsync(null, new[] { ProcessId }, null, ct),
					"attachable process lookup")
					.FirstOrDefault();
				if (attachable == null)
					return "Process " + ProcessId + " is not attachable by dnSpy.";

				return RunOnUIThread(() => {
					attachable.Attach();
					return "Attach requested for PID " + attachable.ProcessId + " (" + attachable.Name + ").";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_List_Processes", MCPCmdDescription = "Lists all processes currently being debugged.")]
		public static string DbgListProcesses() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var processes = Global.MyDbgManager.Processes;
					if (processes.Length == 0)
						return "No processes are currently being debugged.";

					var sb = new StringBuilder();
					foreach (var process in processes.OrderBy(p => p.Id)) {
						sb.Append("PID=").Append(process.Id);
						sb.Append(" Name=").Append(string.IsNullOrWhiteSpace(process.Name) ? "<unknown>" : process.Name);
						sb.Append(" State=").Append(process.State);
						sb.Append(" Running=").Append(process.IsRunning);
						sb.Append(" Bitness=").Append(process.Bitness);
						sb.Append(" Threads=").Append(process.Threads.Length);
						sb.Append(" RuntimeCount=").Append(process.Runtimes.Length);
						if (!string.IsNullOrWhiteSpace(process.Filename))
							sb.Append(" File=").Append(SanitizeForSingleLine(process.Filename, 180));
						sb.AppendLine();
					}
					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Select_Process", MCPCmdDescription = "Sets the current dnSpy debug process context by process id.")]
		public static string DbgSelectProcess(int ProcessId) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var process = GetDebuggedProcess(ProcessId);
					if (process == null)
						return "Process " + ProcessId + " is not being debugged.";

					Global.MyDbgManager.CurrentProcess.Current = process;
					return "Current process set to PID " + process.Id + ".";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_List_Threads", MCPCmdDescription = "Lists threads from the current or all debugged processes.")]
		public static string DbgListThreads() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var processes = Global.MyDbgManager.Processes;
					if (processes.Length == 0)
						return "No active debug session.";

					var currentThread = Global.MyDbgManager.CurrentThread.Current;
					var sb = new StringBuilder();
					foreach (var process in processes.OrderBy(p => p.Id)) {
						sb.AppendLine("Process " + process.Id + ":");
						foreach (var thread in process.Threads.OrderBy(t => t.Id)) {
							var marker = currentThread != null && thread == currentThread ? "*" : " ";
							sb.Append(marker)
								.Append(" TID=").Append(thread.Id)
								.Append(" ManagedId=").Append(thread.ManagedId.HasValue ? thread.ManagedId.Value.ToString() : "<n/a>")
								.Append(" Name=").Append(string.IsNullOrWhiteSpace(thread.UIName) ? thread.Name : thread.UIName)
								.Append(" Kind=").Append(thread.Kind)
								.Append(" Suspended=").Append(thread.SuspendedCount)
								.Append(" State=").Append(FormatThreadState(thread))
								.AppendLine();
						}
						if (process.Threads.Length == 0)
							sb.AppendLine("  <no threads>");
					}
					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Select_Thread", MCPCmdDescription = "Sets the current dnSpy debug thread context by thread id.")]
		public static string DbgSelectThread(ulong ThreadId, int ProcessId = 0) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var thread = GetDebuggedThread(ThreadId, ProcessId);
					if (thread == null)
						return "Thread " + ThreadId + " was not found in the active debug session.";

					Global.MyDbgManager.CurrentThread.Current = thread;
					return "Current thread set to " + thread.Id + ".";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_List_Modules", MCPCmdDescription = "Lists modules loaded in the current or all debugged runtimes.")]
		public static string DbgListModules(int ProcessId = 0) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var processes = ProcessId > 0
						? Global.MyDbgManager.Processes.Where(p => p.Id == ProcessId).ToArray()
						: Global.MyDbgManager.Processes;

					if (processes.Length == 0)
						return ProcessId > 0 ? "Process " + ProcessId + " is not being debugged." : "No active debug session.";

					var modules = processes
						.SelectMany(process => process.Runtimes)
						.SelectMany(runtime => runtime.Modules)
						.OrderBy(module => module.Process.Id)
						.ThenBy(module => module.Runtime.Name, StringComparer.OrdinalIgnoreCase)
						.ThenBy(module => module.Order)
						.ToArray();

					if (modules.Length == 0)
						return "No runtime modules are currently loaded.";

					var sb = new StringBuilder();
					foreach (var module in modules)
						sb.AppendLine(FormatModule(module));
					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_CallStack", MCPCmdDescription = "Returns the current call stack from dnSpy's active debug frame list.")]
		public static string DbgGetCallStack(int MaxFrames = 20) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var callStackService = Global.MyDbgCallStackService;
					if (callStackService == null)
						return "Call stack service is not available.";

					var framesInfo = callStackService.Frames;
					if (framesInfo.Frames.Count == 0)
						return "The debugger is not currently paused on a stack frame.";

					var maxFrames = NormalizeMaxResults(MaxFrames, 20, 200);
					var sb = new StringBuilder();
					var frames = framesInfo.Frames.Take(maxFrames).ToArray();
					for (int i = 0; i < frames.Length; i++) {
						var frame = frames[i];
						var activeMarker = i == framesInfo.ActiveFrameIndex ? "*" : " ";
						sb.Append(activeMarker).Append(" #").Append(i);
						sb.Append(" Token=");
						sb.Append(frame.HasFunctionToken ? "0x" + frame.FunctionToken.ToString("X8") : "<none>");
						sb.Append(" ILOffset=0x").Append(frame.FunctionOffset.ToString("X"));
						if (frame.Module != null)
							sb.Append(" Module=").Append(frame.Module.Name);
						if (frame.Location is DbgDotNetCodeLocation dotNetLocation)
							sb.Append(" Location=").Append("token=0x").Append(dotNetLocation.Token.ToString("X8")).Append(" il=0x").Append(dotNetLocation.Offset.ToString("X"));
						sb.AppendLine();
					}

					if (framesInfo.Frames.Count > maxFrames || framesInfo.FramesTruncated)
						sb.AppendLine("... call stack truncated");

					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Select_Frame", MCPCmdDescription = "Sets the active call stack frame by zero-based frame index.")]
		public static string DbgSelectFrame(int FrameIndex) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var callStackService = Global.MyDbgCallStackService;
					if (callStackService == null)
						return "Call stack service is not available.";
					if (FrameIndex < 0 || FrameIndex >= callStackService.Frames.Frames.Count)
						return "FrameIndex is out of range.";

					callStackService.ActiveFrameIndex = FrameIndex;
					return "Active frame set to #" + FrameIndex + ".";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Locals", MCPCmdDescription = "Returns locals and parameters from the active debug frame, including direct child values.")]
		public static string DbgGetLocals(int MaxChildrenPerNode = 8, bool ShowCompilerGenerated = false, bool ShowDecompilerGenerated = false, bool ShowRawLocals = false) {
			try {
				var serviceError = EnsureDebuggerEvaluationServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					using (var activeEval = CreateActiveEvaluationContext()) {
						var maxChildren = NormalizeMaxResults(MaxChildrenPerNode, 8, 128);
						var nodeOptions = GetValueNodeOptions(hideCompilerGeneratedMembers: !ShowCompilerGenerated, noFuncEval: true);
						var localsOptions = GetLocalsProviderOptions(ShowCompilerGenerated, ShowDecompilerGenerated, ShowRawLocals);
						var locals = activeEval.Language.LocalsProvider.GetNodes(activeEval.EvalInfo, nodeOptions, localsOptions);
						if (locals.Length == 0)
							return "No locals or parameters are available for the active frame.";

						var sb = new StringBuilder();
						var rootNodes = new List<DbgObject>();
						try {
							for (int i = 0; i < locals.Length; i++) {
								var valueNode = locals[i].ValueNode;
								rootNodes.Add(valueNode);
								AppendValueNodeSummary(sb, activeEval.EvalInfo, valueNode, "[" + i + "] ", allowFuncEval: false, kind: locals[i].Kind.ToString());
								AppendValueNodeChildren(sb, activeEval.EvalInfo, valueNode, maxChildren, allowFuncEval: false, noFuncEval: true, indentPrefix: "    ");
							}
							return sb.ToString();
						}
						finally {
							CloseDebugObjects(rootNodes);
						}
					}
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Autos", MCPCmdDescription = "Returns auto-window style values inferred by dnSpy for the active debug frame.")]
		public static string DbgGetAutos(int MaxChildrenPerNode = 8) {
			try {
				var serviceError = EnsureDebuggerEvaluationServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					using (var activeEval = CreateActiveEvaluationContext()) {
						var maxChildren = NormalizeMaxResults(MaxChildrenPerNode, 8, 128);
						var autos = activeEval.Language.AutosProvider.GetNodes(activeEval.EvalInfo, GetValueNodeOptions(noFuncEval: true));
						if (autos.Length == 0)
							return "dnSpy did not provide any auto values for the active frame.";

						var sb = new StringBuilder();
						try {
							for (int i = 0; i < autos.Length; i++) {
								AppendValueNodeSummary(sb, activeEval.EvalInfo, autos[i], "[" + i + "] ", allowFuncEval: false);
								AppendValueNodeChildren(sb, activeEval.EvalInfo, autos[i], maxChildren, allowFuncEval: false, noFuncEval: true, indentPrefix: "    ");
							}
							return sb.ToString();
						}
						finally {
							CloseDebugObjects(autos);
						}
					}
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Return_Values", MCPCmdDescription = "Returns any debugger-tracked return values for the active debug frame.")]
		public static string DbgGetReturnValues(int MaxChildrenPerNode = 8) {
			try {
				var serviceError = EnsureDebuggerEvaluationServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					using (var activeEval = CreateActiveEvaluationContext()) {
						var maxChildren = NormalizeMaxResults(MaxChildrenPerNode, 8, 128);
						var returnValues = activeEval.Language.ReturnValuesProvider.GetNodes(activeEval.EvalInfo, GetValueNodeOptions(noFuncEval: true));
						if (returnValues.Length == 0)
							return "dnSpy has no tracked return values for the active frame.";

						var sb = new StringBuilder();
						try {
							for (int i = 0; i < returnValues.Length; i++) {
								AppendValueNodeSummary(sb, activeEval.EvalInfo, returnValues[i], "[" + i + "] ", allowFuncEval: false);
								AppendValueNodeChildren(sb, activeEval.EvalInfo, returnValues[i], maxChildren, allowFuncEval: false, noFuncEval: true, indentPrefix: "    ");
							}
							return sb.ToString();
						}
						finally {
							CloseDebugObjects(returnValues);
						}
					}
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Evaluate_Expression", MCPCmdDescription = "Evaluates an expression in the active debug frame and returns the formatted result plus direct child values.")]
		public static string DbgEvaluateExpression(string Expression, bool NoSideEffects = true, int MaxChildrenPerNode = 8) {
			try {
				var serviceError = EnsureDebuggerEvaluationServices();
				if (serviceError != null)
					return serviceError;
				if (string.IsNullOrWhiteSpace(Expression))
					return "Expression cannot be empty.";

				return RunOnDebugDispatcher(() => {
					using (var activeEval = CreateActiveEvaluationContext()) {
						var maxChildren = NormalizeMaxResults(MaxChildrenPerNode, 8, 128);
						var allowFuncEval = !NoSideEffects;
						var result = activeEval.Language.ValueNodeFactory.Create(
							activeEval.EvalInfo,
							Expression,
							GetValueNodeOptions(hideCompilerGeneratedMembers: false, noFuncEval: NoSideEffects),
							GetExpressionEvaluationOptions(NoSideEffects),
							expressionEvaluatorState: null);

						try {
							var sb = new StringBuilder();
							sb.AppendLine("Expression: " + Expression);
							sb.AppendLine("CreateResultCausesSideEffects: " + result.CausesSideEffects);
							AppendValueNodeSummary(sb, activeEval.EvalInfo, result.ValueNode, "Root: ", allowFuncEval);
							AppendValueNodeChildren(sb, activeEval.EvalInfo, result.ValueNode, maxChildren, allowFuncEval, noFuncEval: NoSideEffects, indentPrefix: "    ");
							return sb.ToString();
						}
						finally {
							CloseDebugObjects(new DbgObject[] { result.ValueNode });
						}
					}
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Value_Children", MCPCmdDescription = "Evaluates an expression in the active debug frame and expands only its direct child values.")]
		public static string DbgGetValueChildren(string Expression, int MaxChildren = 32, bool NoSideEffects = true) {
			try {
				var serviceError = EnsureDebuggerEvaluationServices();
				if (serviceError != null)
					return serviceError;
				if (string.IsNullOrWhiteSpace(Expression))
					return "Expression cannot be empty.";

				return RunOnDebugDispatcher(() => {
					using (var activeEval = CreateActiveEvaluationContext()) {
						var maxChildren = NormalizeMaxResults(MaxChildren, 32, 128);
						var allowFuncEval = !NoSideEffects;
						var result = activeEval.Language.ValueNodeFactory.Create(
							activeEval.EvalInfo,
							Expression,
							GetValueNodeOptions(hideCompilerGeneratedMembers: false, noFuncEval: NoSideEffects),
							GetExpressionEvaluationOptions(NoSideEffects),
							expressionEvaluatorState: null);

						try {
							var sb = new StringBuilder();
							sb.AppendLine("Expression: " + Expression);
							AppendValueNodeSummary(sb, activeEval.EvalInfo, result.ValueNode, "Root: ", allowFuncEval);
							AppendValueNodeChildren(sb, activeEval.EvalInfo, result.ValueNode, maxChildren, allowFuncEval, noFuncEval: NoSideEffects, indentPrefix: "    ");
							return sb.ToString();
						}
						finally {
							CloseDebugObjects(new DbgObject[] { result.ValueNode });
						}
					}
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Get_Current_Exception", MCPCmdDescription = "Returns exception details for the current break state if the debugger is stopped on an exception.")]
		public static string DbgGetCurrentException() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var runtime = Global.MyDbgManager.CurrentRuntime.Current;
					if (runtime == null)
						return "No current debug runtime.";

					var exceptionInfo = runtime.BreakInfos
						.Where(info => info.Kind == DbgBreakInfoKind.Message && info.Data is DbgMessageExceptionThrownEventArgs)
						.Select(info => (DbgMessageExceptionThrownEventArgs)info.Data)
						.LastOrDefault();

					if (exceptionInfo == null)
						return "The current break state does not contain an exception event.";

					return FormatCurrentException(exceptionInfo.Exception);
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_List_Breakpoints", MCPCmdDescription = "Lists all debugger code breakpoints currently registered in dnSpy.")]
		public static string DbgListBreakpoints() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var breakpointService = Global.MyDbgCodeBreakpointsService;
					if (breakpointService == null)
						return "Breakpoint service is not available.";

					var breakpoints = breakpointService.Breakpoints.OrderBy(bp => bp.Id).ToArray();
					if (breakpoints.Length == 0)
						return "No breakpoints are registered.";

					var sb = new StringBuilder();
					foreach (var breakpoint in breakpoints)
						sb.AppendLine(FormatBreakpoint(breakpoint));
					return sb.ToString();
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Add_Breakpoint", MCPCmdDescription = "Adds a .NET IL breakpoint by assembly, namespace, class, method, optional method signature filter and IL offset.")]
		public static string DbgAddBreakpoint(string Assembly, string Namespace, string ClassName, string MethodName, string MethodSignature = null, int ILOffset = 0) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;
				if (Global.MyDbgDotNetBreakpointFactory == null)
					return ".NET breakpoint factory service is not available.";
				if (Global.MyDbgModuleIdProvider == null)
					return "Debugger module id provider service is not available.";

				var target = ResolveBreakpointTarget(Assembly, Namespace, ClassName, MethodName, MethodSignature, ILOffset);

				return RunOnDebugDispatcher(() => {
					var dbgModule = FindDebuggerModule(target);
					if (dbgModule == null)
						return "The target module '" + target.ModuleName + "' is not loaded in the active debug session.";

					var moduleId = Global.MyDbgModuleIdProvider.GetModuleId(dbgModule);
					if (!moduleId.HasValue)
						return "dnSpy could not resolve a module id for " + dbgModule.Name + ".";

					var breakpoint = Global.MyDbgDotNetBreakpointFactory.Create(moduleId.Value, target.MethodToken, target.ILOffset);
					if (breakpoint == null)
						return "A breakpoint already exists at " + target.TypeFullName + "." + target.MethodName + " IL 0x" + target.ILOffset.ToString("X") + ".";

					return "Breakpoint " + breakpoint.Id + " added at " + target.TypeFullName + "." + target.MethodName + " IL 0x" + target.ILOffset.ToString("X") + ".";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Add_Current_Frame_Breakpoint", MCPCmdDescription = "Adds a breakpoint at the current active debug frame location.")]
		public static string DbgAddCurrentFrameBreakpoint() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var callStackService = Global.MyDbgCallStackService;
					var breakpointService = Global.MyDbgCodeBreakpointsService;
					if (callStackService == null || breakpointService == null)
						return "Debugger call stack or breakpoint service is not available.";

					var frame = callStackService.ActiveFrame;
					if (frame == null)
						return "There is no active frame.";
					if (frame.Location == null)
						return "The active frame has no code location.";

					var locationClone = frame.Location.Clone();
					var breakpoint = breakpointService.Add(new DbgCodeBreakpointInfo(locationClone, new DbgCodeBreakpointSettings { IsEnabled = true }));
					if (breakpoint == null) {
						locationClone.Close();
						return "A breakpoint already exists at the current frame.";
					}

					return "Breakpoint " + breakpoint.Id + " added at the current frame.";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Remove_Breakpoint", MCPCmdDescription = "Removes a debugger breakpoint by breakpoint id.")]
		public static string DbgRemoveBreakpoint(int BreakpointId) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var breakpointService = Global.MyDbgCodeBreakpointsService;
					if (breakpointService == null)
						return "Breakpoint service is not available.";

					var breakpoint = breakpointService.Breakpoints.FirstOrDefault(bp => bp.Id == BreakpointId);
					if (breakpoint == null)
						return "Breakpoint " + BreakpointId + " was not found.";

					breakpointService.Remove(new[] { breakpoint });
					return "Breakpoint " + BreakpointId + " removed.";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Break_All", MCPCmdDescription = "Pauses all debugged processes.")]
		public static string DbgBreakAll() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var dbgManager = Global.MyDbgManager;
					if (!dbgManager.IsDebugging)
						return "No active debug session.";

					dbgManager.BreakAll();
					return "Break all requested.";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Continue", MCPCmdDescription = "Continues all paused debugged processes.")]
		public static string DbgContinue() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var dbgManager = Global.MyDbgManager;
					if (!dbgManager.IsDebugging)
						return "No active debug session.";

					dbgManager.RunAll();
					return "Continue requested for all debugged processes.";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Step_Into", MCPCmdDescription = "Performs a debugger step-into on the current thread.")]
		public static string DbgStepInto() => ExecuteStep(DbgStepKind.StepInto, "Step into");

		[Command("Dbg_Step_Over", MCPCmdDescription = "Performs a debugger step-over on the current thread.")]
		public static string DbgStepOver() => ExecuteStep(DbgStepKind.StepOver, "Step over");

		[Command("Dbg_Step_Out", MCPCmdDescription = "Performs a debugger step-out on the current thread.")]
		public static string DbgStepOut() => ExecuteStep(DbgStepKind.StepOut, "Step out");

		[Command("Dbg_Detach_All", MCPCmdDescription = "Detaches dnSpy from all debugged processes only when dnSpy reports it can do so without terminating any target.")]
		public static string DbgDetachAll() {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var dbgManager = Global.MyDbgManager;
					if (!dbgManager.IsDebugging)
						return "No active debug session.";
					if (!dbgManager.CanDetachWithoutTerminating)
						return "Detach was refused because dnSpy reports one or more debugged processes would be terminated. Use dnSpy UI to inspect the session before forcing a stop.";

					dbgManager.DetachAll();
					return "Detach requested for all debugged processes.";
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}

		[Command("Dbg_Read_Memory", MCPCmdDescription = "Reads memory from the current or specified debugged process and returns a hex dump.")]
		public static string DbgReadMemory(ulong Address, int Size = 64, int ProcessId = 0) {
			try {
				var serviceError = EnsureDebuggerServices();
				if (serviceError != null)
					return serviceError;

				return RunOnDebugDispatcher(() => {
					var process = GetDebuggedProcess(ProcessId);
					if (process == null)
						return ProcessId > 0 ? "Process " + ProcessId + " is not being debugged." : "No active debug process.";

					var size = Math.Max(1, Math.Min(Size, 4096));
					var bytes = process.ReadMemory(Address, size);
					return FormatMemoryBlock(Address, bytes);
				});
			}
			catch (Exception ex) {
				return "Exception: " + ex.Message;
			}
		}
	}
}
