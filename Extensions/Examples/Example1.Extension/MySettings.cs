using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace Example1.Extension {
	class MySettings : ViewModelBase {
		public string ServerHost {
			get => serverHost;
			set {
				var newValue = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
				if (serverHost != newValue) {
					serverHost = newValue;
					OnPropertyChanged(nameof(ServerHost));
				}
			}
		}
		string serverHost = "127.0.0.1";

		public int ServerPort {
			get => serverPort;
			set {
				var newValue = value <= 0 || value > 65535 ? 3003 : value;
				if (serverPort != newValue) {
					serverPort = newValue;
					OnPropertyChanged(nameof(ServerPort));
				}
			}
		}
		int serverPort = 3003;

		public bool AutoStartServer {
			get => autoStartServer;
			set {
				if (autoStartServer != value) {
					autoStartServer = value;
					OnPropertyChanged(nameof(AutoStartServer));
				}
			}
		}
		bool autoStartServer = true;

		public bool ShowStartupPrompt {
			get => showStartupPrompt;
			set {
				if (showStartupPrompt != value) {
					showStartupPrompt = value;
					OnPropertyChanged(nameof(ShowStartupPrompt));
				}
			}
		}
		bool showStartupPrompt = true;

		public bool VerboseLogging {
			get => verboseLogging;
			set {
				if (verboseLogging != value) {
					verboseLogging = value;
					OnPropertyChanged(nameof(VerboseLogging));
				}
			}
		}
		bool verboseLogging;

		public bool OpenAssemblyExplorerOnStartup {
			get => openAssemblyExplorerOnStartup;
			set {
				if (openAssemblyExplorerOnStartup != value) {
					openAssemblyExplorerOnStartup = value;
					OnPropertyChanged(nameof(OpenAssemblyExplorerOnStartup));
				}
			}
		}
		bool openAssemblyExplorerOnStartup = true;

		public bool OpenMcpLogOnStartup {
			get => openMcpLogOnStartup;
			set {
				if (openMcpLogOnStartup != value) {
					openMcpLogOnStartup = value;
					OnPropertyChanged(nameof(OpenMcpLogOnStartup));
				}
			}
		}
		bool openMcpLogOnStartup = true;

		public MySettings Clone() => CopyTo(new MySettings());

		public MySettings CopyTo(MySettings other) {
			other.ServerHost = ServerHost;
			other.ServerPort = ServerPort;
			other.AutoStartServer = AutoStartServer;
			other.ShowStartupPrompt = ShowStartupPrompt;
			other.VerboseLogging = VerboseLogging;
			other.OpenAssemblyExplorerOnStartup = OpenAssemblyExplorerOnStartup;
			other.OpenMcpLogOnStartup = OpenMcpLogOnStartup;
			return other;
		}
	}

	[Export(typeof(MySettings))]
	sealed class MySettingsImpl : MySettings {
		static readonly Guid SETTINGS_GUID = new Guid("A308405D-0DF5-4C56-8B1E-8CE7BA6365E1");

		readonly ISettingsService settingsService;

		[ImportingConstructor]
		MySettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			ServerHost = sect.Attribute<string>(nameof(ServerHost)) ?? ServerHost;
			ServerPort = sect.Attribute<int?>(nameof(ServerPort)) ?? ServerPort;
			AutoStartServer = sect.Attribute<bool?>(nameof(AutoStartServer)) ?? AutoStartServer;
			ShowStartupPrompt = sect.Attribute<bool?>(nameof(ShowStartupPrompt)) ?? ShowStartupPrompt;
			VerboseLogging = sect.Attribute<bool?>(nameof(VerboseLogging)) ?? VerboseLogging;
			OpenAssemblyExplorerOnStartup = sect.Attribute<bool?>(nameof(OpenAssemblyExplorerOnStartup)) ?? OpenAssemblyExplorerOnStartup;
			OpenMcpLogOnStartup = sect.Attribute<bool?>(nameof(OpenMcpLogOnStartup)) ?? OpenMcpLogOnStartup;
			PropertyChanged += MySettingsImpl_PropertyChanged;
		}

		void MySettingsImpl_PropertyChanged(object sender, PropertyChangedEventArgs e) {
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(ServerHost), ServerHost);
			sect.Attribute(nameof(ServerPort), ServerPort);
			sect.Attribute(nameof(AutoStartServer), AutoStartServer);
			sect.Attribute(nameof(ShowStartupPrompt), ShowStartupPrompt);
			sect.Attribute(nameof(VerboseLogging), VerboseLogging);
			sect.Attribute(nameof(OpenAssemblyExplorerOnStartup), OpenAssemblyExplorerOnStartup);
			sect.Attribute(nameof(OpenMcpLogOnStartup), OpenMcpLogOnStartup);
		}
	}
}
