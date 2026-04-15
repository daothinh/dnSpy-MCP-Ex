using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Windows;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Menus;

namespace Example1.Extension {
	static class MainMenuConstants {
		public const string APP_MENU_EXTENSION = "4E6829A6-AEA0-4803-9344-D19BF0A74DA1";
		public const string GROUP_EXTENSION_SERVER = "0,73BEBC37-387A-4004-8076-A1A90A17611B";
		public const string GROUP_EXTENSION_SETTINGS = "10,C21B8B99-A2E4-474F-B4BC-4CF348ECBD0A";
	}

	static class McpMenuHelpers {
		public static string StartServer(MySettings settings) {
			try {
				var server = Global.EnsureServer(settings);
				server.TryStart(out var message);
				return message;
			}
			catch (Exception ex) {
				return "Failed to start MCP server: " + ex.Message;
			}
		}

		public static string StopServer() {
			var server = Global.MySimpleMCPServer;
			if (server == null)
				return "MCP server has not been initialized yet.";
			server.TryStop(out var message);
			return message;
		}

		public static void CopyText(string text, string successMessage) {
			try {
				Clipboard.SetText(text);
				MsgBox.Instance.Show(successMessage);
			}
			catch (ExternalException ex) {
				MsgBox.Instance.Show("Clipboard is busy: " + ex.Message);
			}
		}

		public static void ShowMcpLog() => McpOutputLog.Show();

		public static void ClearMcpLog() => McpOutputLog.Clear();
	}

	[ExportMenu(OwnerGuid = MenuConstants.APP_MENU_GUID, Guid = MainMenuConstants.APP_MENU_EXTENSION, Order = MenuConstants.ORDER_APP_MENU_DEBUG + 0.1, Header = "_Extension")]
	sealed class ExtensionMenu : IMenu {
	}

	[ExportMenuItem(OwnerGuid = MainMenuConstants.APP_MENU_EXTENSION, Header = "Start MCP Server", Group = MainMenuConstants.GROUP_EXTENSION_SERVER, Order = 0)]
	sealed class StartMcpServerCommand : MenuItemBase {
		readonly MySettings mySettings;

		[ImportingConstructor]
		StartMcpServerCommand(MySettings mySettings) => this.mySettings = mySettings;

		public override void Execute(IMenuItemContext context) => MsgBox.Instance.Show(McpMenuHelpers.StartServer(mySettings));
	}

	[ExportMenuItem(OwnerGuid = MainMenuConstants.APP_MENU_EXTENSION, Header = "Stop MCP Server", Group = MainMenuConstants.GROUP_EXTENSION_SERVER, Order = 10)]
	sealed class StopMcpServerCommand : MenuItemBase {
		public override void Execute(IMenuItemContext context) => MsgBox.Instance.Show(McpMenuHelpers.StopServer());
	}

	[ExportMenuItem(OwnerGuid = MainMenuConstants.APP_MENU_EXTENSION, Header = "MCP Server Status", Group = MainMenuConstants.GROUP_EXTENSION_SERVER, Order = 20)]
	sealed class ShowMcpServerStatusCommand : MenuItemBase {
		public override void Execute(IMenuItemContext context) => MsgBox.Instance.Show(MCPCommands.GetServerStatus());
	}

	[ExportMenuItem(OwnerGuid = MainMenuConstants.APP_MENU_EXTENSION, Header = "Show MCP Log", Group = MainMenuConstants.GROUP_EXTENSION_SERVER, Order = 30)]
	sealed class ShowMcpLogCommand : MenuItemBase {
		public override void Execute(IMenuItemContext context) => McpMenuHelpers.ShowMcpLog();
	}

	[ExportMenuItem(OwnerGuid = MainMenuConstants.APP_MENU_EXTENSION, Header = "Clear MCP Log", Group = MainMenuConstants.GROUP_EXTENSION_SERVER, Order = 40)]
	sealed class ClearMcpLogCommand : MenuItemBase {
		public override void Execute(IMenuItemContext context) => McpMenuHelpers.ClearMcpLog();
	}

	[ExportMenuItem(OwnerGuid = MainMenuConstants.APP_MENU_EXTENSION, Header = "Auto Start MCP Server", Group = MainMenuConstants.GROUP_EXTENSION_SETTINGS, Order = 0)]
	sealed class ToggleAutoStartCommand : MenuItemBase {
		readonly MySettings mySettings;

		[ImportingConstructor]
		ToggleAutoStartCommand(MySettings mySettings) => this.mySettings = mySettings;

		public override bool IsChecked(IMenuItemContext context) => mySettings.AutoStartServer;
		public override void Execute(IMenuItemContext context) => mySettings.AutoStartServer = !mySettings.AutoStartServer;
	}

	[ExportMenuItem(OwnerGuid = MainMenuConstants.APP_MENU_EXTENSION, Header = "Startup Prompt", Group = MainMenuConstants.GROUP_EXTENSION_SETTINGS, Order = 10)]
	sealed class ToggleStartupPromptCommand : MenuItemBase {
		readonly MySettings mySettings;

		[ImportingConstructor]
		ToggleStartupPromptCommand(MySettings mySettings) => this.mySettings = mySettings;

		public override bool IsChecked(IMenuItemContext context) => mySettings.ShowStartupPrompt;
		public override void Execute(IMenuItemContext context) => mySettings.ShowStartupPrompt = !mySettings.ShowStartupPrompt;
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "Copy MCP SSE URL", Group = MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS, Order = 1000)]
	sealed class CopyMcpSseUrlCommand : MenuItemBase {
		readonly MySettings mySettings;

		[ImportingConstructor]
		CopyMcpSseUrlCommand(MySettings mySettings) => this.mySettings = mySettings;

		public override void Execute(IMenuItemContext context) {
			var server = Global.MySimpleMCPServer ?? Global.EnsureServer(mySettings);
			McpMenuHelpers.CopyText(server.SseEndpoint, "Copied: " + server.SseEndpoint);
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "MCP Log", Group = MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS, Order = 1010)]
	sealed class ShowMcpLogViewCommand : MenuItemBase {
		public override void Execute(IMenuItemContext context) => McpMenuHelpers.ShowMcpLog();
	}
}
