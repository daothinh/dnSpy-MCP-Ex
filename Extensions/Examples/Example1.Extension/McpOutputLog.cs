using System;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.ToolWindows.App;

namespace Example1.Extension {
	static class McpOutputLog {
		static readonly object gate = new object();
		static readonly Guid outputToolWindowGuid = new Guid("90A45E97-727E-4F31-8692-06E19218D99A");

		public static readonly Guid PaneGuid = new Guid("E9419F18-C453-4D39-9D6D-80CC04D4C08A");

		static IOutputService outputService;
		static IOutputTextPane outputPane;
		static IDsToolWindowService toolWindowService;
		static MySettings settings;

		static bool VerboseLoggingEnabled => settings?.VerboseLogging ?? false;

		public static void Initialize(IOutputService newOutputService, IDsToolWindowService newToolWindowService, MySettings newSettings) {
			if (newOutputService == null)
				return;

			lock (gate) {
				outputService = newOutputService;
				toolWindowService = newToolWindowService;
				settings = newSettings;
				outputPane ??= outputService.Create(PaneGuid, "MCP Log");
			}

			WriteInfo("MCP log pane initialized.");
		}

		public static void Show() {
			lock (gate) {
				if (outputService == null || outputPane == null)
					return;

				toolWindowService?.Show(outputToolWindowGuid);
				outputService.Select(PaneGuid);
			}
		}

		public static void Clear() {
			lock (gate) {
				outputPane?.Clear();
			}
		}

		public static void WriteInfo(string message) => WriteLine(message, TextColor.DebugLogExtensionMessage);

		public static void WriteWarning(string message) => WriteLine(message, TextColor.Yellow);

		public static void WriteError(string message) => WriteLine(message, TextColor.Error);

		public static void WriteVerbose(string message) => WriteLine(message, TextColor.Gray, verboseOnly: true);

		public static void WriteLine(string message, TextColor color = TextColor.Text, bool verboseOnly = false) {
			if (string.IsNullOrWhiteSpace(message))
				return;
			if (verboseOnly && !VerboseLoggingEnabled)
				return;

			var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message;

			lock (gate) {
				try {
					outputPane?.WriteLine(color, line);
				}
				catch {
				}
			}
		}
	}
}
