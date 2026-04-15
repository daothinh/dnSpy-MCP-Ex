using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Metadata;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.ToolWindows.App;

namespace Example1.Extension {
	[ExportAutoLoaded(Order = double.MaxValue)]
	sealed class McpStartupLoader : IAutoLoaded {
		static int initialized;

		[ImportingConstructor]
		McpStartupLoader(
			IAppWindow appWindow,
			MySettings settings,
			IDocumentTreeView treeView,
			IDocumentTabService tabService,
			[ImportMany] IEnumerable<IOutputService> outputServices,
			[ImportMany] IEnumerable<IDsToolWindowService> toolWindowServices,
			[ImportMany] IEnumerable<DbgManager> dbgManagers,
			[ImportMany] IEnumerable<DebuggerSettings> debuggerSettings,
			[ImportMany] IEnumerable<AttachableProcessesService> attachableProcessesServices,
			[ImportMany] IEnumerable<DbgCodeBreakpointsService> dbgCodeBreakpointsServices,
			[ImportMany] IEnumerable<DbgCallStackService> dbgCallStackServices,
			[ImportMany] IEnumerable<DbgDotNetBreakpointFactory> dbgDotNetBreakpointFactories,
			[ImportMany] IEnumerable<DbgLanguageService> dbgLanguageServices,
			[ImportMany] IEnumerable<DbgModuleIdProvider> dbgModuleIdProviders,
			[ImportMany] IEnumerable<DbgMetadataService> dbgMetadataServices) {
			appWindow.MainWindow.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
				Initialize(
					appWindow,
					settings,
					treeView,
					tabService,
					outputServices.FirstOrDefault(),
					toolWindowServices.FirstOrDefault(),
					dbgManagers.FirstOrDefault(),
					debuggerSettings.FirstOrDefault(),
					attachableProcessesServices.FirstOrDefault(),
					dbgCodeBreakpointsServices.FirstOrDefault(),
					dbgCallStackServices.FirstOrDefault(),
					dbgDotNetBreakpointFactories.FirstOrDefault(),
					dbgLanguageServices.FirstOrDefault(),
					dbgModuleIdProviders.FirstOrDefault(),
					dbgMetadataServices.FirstOrDefault())));
		}

		static void Initialize(
			IAppWindow appWindow,
			MySettings settings,
			IDocumentTreeView treeView,
			IDocumentTabService tabService,
			IOutputService outputService,
			IDsToolWindowService toolWindowService,
			DbgManager dbgManager,
			DebuggerSettings debuggerSettings,
			AttachableProcessesService attachableProcessesService,
			DbgCodeBreakpointsService dbgCodeBreakpointsService,
			DbgCallStackService dbgCallStackService,
			DbgDotNetBreakpointFactory dbgDotNetBreakpointFactory,
			DbgLanguageService dbgLanguageService,
			DbgModuleIdProvider dbgModuleIdProvider,
			DbgMetadataService dbgMetadataService) {
			if (Interlocked.Exchange(ref initialized, 1) != 0)
				return;

			try {
				Global.MyTreeView = treeView;
				Global.MyAppWindow = appWindow;
				Global.MyDocumentTabService = tabService;
				Global.MySettings = settings;
				Global.MyDbgManager = dbgManager;
				Global.MyDebuggerSettings = debuggerSettings;
				Global.MyAttachableProcessesService = attachableProcessesService;
				Global.MyDbgCodeBreakpointsService = dbgCodeBreakpointsService;
				Global.MyDbgCallStackService = dbgCallStackService;
				Global.MyDbgDotNetBreakpointFactory = dbgDotNetBreakpointFactory;
				Global.MyDbgLanguageService = dbgLanguageService;
				Global.MyDbgModuleIdProvider = dbgModuleIdProvider;
				Global.MyDbgMetadataService = dbgMetadataService;
				McpOutputLog.Initialize(outputService, toolWindowService, settings);
				McpOutputLog.WriteInfo("dnSpy MCP extension initialization started.");

				var server = Global.EnsureServer(settings);
				if (settings.AutoStartServer) {
					var shouldStart = true;
					if (settings.ShowStartupPrompt) {
						var result = MsgBox.Instance.Show(
							"dnSpy MCP Server is ready.\n\nStart listening on " + server.SseEndpoint + " ?",
							MsgBoxButton.Yes | MsgBoxButton.No);
						shouldStart = result == MsgBoxButton.Yes;
					}

					if (shouldStart) {
						if (!server.TryStart(out var startMessage))
							MsgBox.Instance.Show(startMessage + "\n\n" + server.LastStartError);
					}
					else {
						McpOutputLog.WriteInfo("MCP server auto-start was skipped by user.");
					}
				}

				if (settings.OpenMcpLogOnStartup)
					McpOutputLog.Show();

				if (toolWindowService != null && settings.OpenAssemblyExplorerOnStartup) {
					var asmExplorerGuid = new Guid("5495EE9F-1EF2-45F3-A320-22A89BFDF731");
					toolWindowService.Show(asmExplorerGuid);
				}
			}
			catch (Exception ex) {
				Interlocked.Exchange(ref initialized, 0);
				MsgBox.Instance.Show("dnSpy MCP Server initialization failed.\n\n" + ex);
			}
		}
	}
}
