using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace PortWorkshop
{
	internal enum SessionState
	{
		Off,
		Connecting,
		SignedOut,       // connected, nobody signed in: the QR code and the password form are offered
		Authenticating,  // a password is being checked
		NeedCode,        // Steam Guard wants a code (CodePrompt says which)
		WaitingApproval, // Steam Guard waits for the sign-in to be approved in the Steam app
		LoggingOn,
		Online,
	}

	/// <summary>
	/// The connection to Steam (SteamKit2), on a thread of its own that runs SteamKit2's callbacks. Signing in is done
	/// with Steam's own authentication: a QR code that the Steam app approves, or the account name and password, which
	/// are used for this one sign-in and not kept. What is kept (in the app's private files, which no other app can
	/// read) is the refresh token Steam hands out for a remembered sign-in, so that later sessions sign in by themselves;
	/// signing out deletes it.
	/// </summary>
	internal static class Session
	{
		public const uint App = 294100;

		private static SteamClient client;
		private static CallbackManager manager;
		private static SteamUser user;
		private static SteamApps apps;
		private static SteamContent content;
		private static PublishedFile publishedFiles;
		private static Client cdn;
		private static Thread pump;
		private static volatile bool wanted, connected;
		private static CancellationTokenSource authCancel;
		private static TaskCompletionSource<string> codeSource;
		private static uint cellId;
		private static byte[] depotKey;
		private static List<Server> servers;

		public static volatile SessionState State = SessionState.Off;
		public static volatile string Message = "";
		public static volatile string Account = "";
		public static volatile string CodePrompt = "";
		public static volatile string QrUrl = "";

		private static string PrivateDir
		{
			get
			{
				string package = File.ReadAllText("/proc/self/cmdline").Split('\0')[0].Trim();
				string dir = Path.Combine("/data/data", package, "files", "steam");
				Directory.CreateDirectory(dir);
				return dir;
			}
		}

		private static string LoginFile => Path.Combine(PrivateDir, "login");

		private static void Log(string text)
		{
			Console.WriteLine("[workshop] " + text);
		}

		/// <summary>An identity for this installation, made once: what Steam sees as this machine.</summary>
		private sealed class Machine : IMachineInfoProvider
		{
			private readonly byte[] guid;

			public Machine()
			{
				string file = Path.Combine(PrivateDir, "machine");
				if (File.Exists(file) && new FileInfo(file).Length == 16)
				{
					guid = File.ReadAllBytes(file);
				}
				else
				{
					guid = RandomNumberGenerator.GetBytes(16);
					File.WriteAllBytes(file, guid);
				}
			}

			public uint LoginId => BitConverter.ToUInt32(guid, 0);
			public byte[] GetMachineGuid() => guid;
			public byte[] GetMacAddress() => guid.Take(6).ToArray();
			public byte[] GetDiskId() => guid.Reverse().ToArray();
		}

		private static Machine machine;

		// SteamKit2 knows Android as nothing at all (EOSType.Unknown); some of Steam's servers then refuse the sign-in with
		// TryAnotherCM. The phone's kernel is a Linux 6.
		private const EOSType OsType = EOSType.Linux6x;

		// A sign-in Steam asked to try on another of its servers: the remembered token is good, the server is not.
		private static volatile bool logOnElsewhere;
		private static int logOnAttempts;

		/// <summary>Connects (and signs in with the remembered token, if there is one). Idempotent.</summary>
		public static void Start()
		{
			wanted = true;
			if (client == null)
			{
				if (File.Exists(Path.Combine(AppContext.BaseDirectory, "workshop-debug.txt")))
				{
					DebugLog.AddListener((category, message) => Console.WriteLine("[steamkit] " + category + ": " + message));
					DebugLog.Enabled = true;
				}
				machine = new Machine();
				SteamConfiguration configuration = SteamConfiguration.Create(b => b.WithMachineInfoProvider(machine).WithProtocolTypes(ProtocolTypes.WebSocket | ProtocolTypes.Tcp).WithServerListProvider(new SteamKit2.Discovery.FileStorageServerListProvider(Path.Combine(PrivateDir, "servers"))));
				client = new SteamClient(configuration);
				manager = new CallbackManager(client);
				user = client.GetHandler<SteamUser>();
				apps = client.GetHandler<SteamApps>();
				content = client.GetHandler<SteamContent>();
				publishedFiles = client.GetHandler<SteamUnifiedMessages>().CreateService<PublishedFile>();
				cdn = new Client(client);
				manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
				manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
				manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
				manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
				pump = new Thread(() =>
				{
					while (true)
					{
						try
						{
							manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
						}
						catch (Exception e)
						{
							Log("callback failed: " + e);
						}
					}
				}) { IsBackground = true, Name = "SteamKit2" };
				pump.Start();
			}
			if (!connected && State != SessionState.Connecting)
			{
				State = SessionState.Connecting;
				Message = "";
				client.Connect();
			}
		}

		/// <summary>Disconnects (the remembered sign-in stays).</summary>
		public static void Stop()
		{
			wanted = false;
			CancelAuth();
			if (client != null && connected)
			{
				client.Disconnect();
			}
		}

		private static void OnConnected(SteamClient.ConnectedCallback callback)
		{
			connected = true;
			Message = "";
			Log("connected");
			if (TryReadLogin(out string account, out string token))
			{
				LogOn(account, token);
			}
			else
			{
				State = SessionState.SignedOut;
				StartQr();
			}
		}

		private static void OnDisconnected(SteamClient.DisconnectedCallback callback)
		{
			connected = false;
			CancelAuth();
			Log("disconnected" + (callback.UserInitiated ? " (asked for)" : ""));
			if (wanted && logOnElsewhere)
			{
				logOnElsewhere = false;
				State = SessionState.Connecting;
				Task.Delay(1000).ContinueWith(_ =>
				{
					if (wanted && !connected)
					{
						client.Connect();
					}
				});
			}
			else if (wanted && !callback.UserInitiated)
			{
				State = SessionState.Connecting;
				Message = Texts.Say("和 Steam 的连接断了，正在重连……", "The connection to Steam was lost, reconnecting...");
				Task.Delay(3000).ContinueWith(_ =>
				{
					if (wanted && !connected)
					{
						client.Connect();
					}
				});
			}
			else
			{
				State = SessionState.Off;
			}
		}

		private static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
		{
			if (callback.Result == EResult.OK)
			{
				logOnAttempts = 0;
				cellId = callback.CellID;
				State = SessionState.Online;
				Message = "";
				Log("signed in as " + Account + " (cell " + cellId + ")");
				return;
			}
			Log("sign-in refused: " + callback.Result + " / " + callback.ExtendedResult);
			bool transient = callback.Result == EResult.TryAnotherCM || callback.Result == EResult.ServiceUnavailable || callback.Result == EResult.Busy || callback.Result == EResult.Timeout || callback.Result == EResult.Fail;
			if (transient && logOnAttempts < 6 && TryReadLogin(out _, out _))
			{
				logOnAttempts++;
				Message = Texts.Say("这台 Steam 服务器没接受，换一台再试……", "That Steam server did not take it, trying another...");
				MarkCurrentServerBad();
				logOnElsewhere = true;
				client.Disconnect();
				return;
			}
			// A remembered token that no longer works (expired, revoked, the password changed): it goes.
			DeleteLogin();
			Account = "";
			State = SessionState.SignedOut;
			Message = Texts.Say("Steam 没有接受登录（" + callback.Result + "），请重新登录。", "Steam did not accept the sign-in (" + callback.Result + "); please sign in again.");
			StartQr();
		}

		/// <summary>The server of this connection goes to the back of the list (SteamKit2 keeps the marking to itself).</summary>
		private static void MarkCurrentServerBad()
		{
			try
			{
				var endPoint = client.CurrentEndPoint;
				var mark = typeof(SteamKit2.Discovery.SmartCMServerList).GetMethod("TryMark", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
				if (endPoint != null && mark != null)
				{
					mark.Invoke(client.Servers, new object[] { endPoint, ProtocolTypes.WebSocket | ProtocolTypes.Tcp, SteamKit2.Discovery.ServerQuality.Bad });
					Log("server " + endPoint + " marked bad");
				}
			}
			catch (Exception e)
			{
				Log("could not mark the server: " + e.Message);
			}
		}

		private static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
		{
			Log("signed off by Steam: " + callback.Result);
			State = SessionState.SignedOut;
			Message = Texts.Say("Steam 让这次登录下线了（" + callback.Result + "）。", "Steam signed this session off (" + callback.Result + ").");
		}

		private static void LogOn(string account, string token)
		{
			Account = account;
			State = SessionState.LoggingOn;
			user.LogOn(new SteamUser.LogOnDetails
			{
				Username = account,
				AccessToken = token,
				ShouldRememberPassword = true,
				LoginID = machine.LoginId,
				ClientOSType = OsType,
			});
		}

		// ---- signing in ----

		private static AuthSessionDetails Details(string account = null, string password = null)
		{
			return new AuthSessionDetails
			{
				Username = account,
				Password = password,
				IsPersistentSession = true,
				DeviceFriendlyName = "RimWorld (Android)",
				ClientOSType = OsType,
				// A QR code is approved by scanning it; Steam Guard only takes part in a password sign-in.
				Authenticator = account != null ? new Guard() : null,
			};
		}

		private static void CancelAuth()
		{
			authCancel?.Cancel();
			authCancel = null;
			codeSource?.TrySetCanceled();
			codeSource = null;
		}

		/// <summary>A QR code for the Steam app to approve; it renews itself until it is used.</summary>
		private static void StartQr()
		{
			CancelAuth();
			if (!connected)
			{
				return;
			}
			var cancel = authCancel = new CancellationTokenSource();
			Task.Run(async () =>
			{
				try
				{
					Log("asking for a QR code");
					QrAuthSession session = await client.Authentication.BeginAuthSessionViaQRAsync(Details());
					QrUrl = session.ChallengeURL;
					Log("QR code ready");
					session.ChallengeURLChanged = () => QrUrl = session.ChallengeURL;
					AuthPollResult result = await session.PollingWaitForResultAsync(cancel.Token);
					Signed(result);
				}
				catch (Exception e) when (cancel.IsCancellationRequested || e is OperationCanceledException)
				{
				}
				catch (Exception e)
				{
					Log("QR sign-in failed: " + e.Message);
					QrUrl = "";
					if (State == SessionState.SignedOut)
					{
						Message = Texts.Say("二维码登录出错：", "The QR sign-in failed: ") + e.Message;
					}
				}
			});
		}

		/// <summary>The account name and password, typed by the player: used for this sign-in only.</summary>
		public static void SignInWithPassword(string account, string password)
		{
			CancelAuth();
			QrUrl = "";
			var cancel = authCancel = new CancellationTokenSource();
			State = SessionState.Authenticating;
			Message = "";
			Task.Run(async () =>
			{
				try
				{
					CredentialsAuthSession session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(Details(account.Trim(), password));
					AuthPollResult result = await session.PollingWaitForResultAsync(cancel.Token);
					Signed(result);
				}
				catch (Exception e) when (cancel.IsCancellationRequested || e is OperationCanceledException)
				{
				}
				catch (Exception e)
				{
					Log("password sign-in failed: " + e.Message);
					State = SessionState.SignedOut;
					Message = e is AuthenticationException auth && auth.Result == EResult.InvalidPassword
						? Texts.Say("账号或密码不对。", "Wrong account name or password.")
						: Texts.Say("登录出错：", "Signing in failed: ") + e.Message;
					StartQr();
				}
			});
		}

		/// <summary>The code Steam Guard asked for.</summary>
		public static void SubmitCode(string code)
		{
			State = SessionState.Authenticating;
			codeSource?.TrySetResult(code.Trim());
		}

		/// <summary>Back to the QR code and the password form (a sign-in under way is given up).</summary>
		public static void Restart()
		{
			State = SessionState.SignedOut;
			Message = "";
			StartQr();
		}

		private static void Signed(AuthPollResult result)
		{
			WriteLogin(result.AccountName, result.RefreshToken);
			LogOn(result.AccountName, result.RefreshToken);
		}

		public static void SignOut()
		{
			DeleteLogin();
			Account = "";
			if (connected)
			{
				user.LogOff();
			}
			depotKey = null;
			State = SessionState.SignedOut;
			Message = "";
			if (connected)
			{
				StartQr();
			}
		}

		/// <summary>Steam Guard, through the window.</summary>
		private sealed class Guard : IAuthenticator
		{
			public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
			{
				return Ask(previousCodeWasIncorrect ? Texts.Say("验证码不对，请再输入一次 Steam 手机令牌里的验证码：", "The code was wrong; enter the code from the Steam Mobile Authenticator again:") : Texts.Say("输入 Steam 手机令牌（Steam App 里）的验证码：", "Enter the code from the Steam Mobile Authenticator (in the Steam app):"));
			}

			public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
			{
				return Ask((previousCodeWasIncorrect ? Texts.Say("验证码不对。", "The code was wrong. ") : "") + Texts.Say("输入 Steam 发到 " + email + " 的验证码：", "Enter the code Steam sent to " + email + ":"));
			}

			public Task<bool> AcceptDeviceConfirmationAsync()
			{
				State = SessionState.WaitingApproval;
				return Task.FromResult(true);
			}

			private static Task<string> Ask(string prompt)
			{
				var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
				codeSource = source;
				CodePrompt = prompt;
				State = SessionState.NeedCode;
				return source.Task;
			}
		}

		private static bool TryReadLogin(out string account, out string token)
		{
			account = token = null;
			try
			{
				if (!File.Exists(LoginFile))
				{
					return false;
				}
				string[] lines = File.ReadAllLines(LoginFile);
				if (lines.Length < 2 || lines[0].Length == 0 || lines[1].Length == 0)
				{
					return false;
				}
				account = lines[0];
				token = lines[1];
				return true;
			}
			catch (Exception e)
			{
				Log("the remembered sign-in could not be read: " + e.Message);
				return false;
			}
		}

		private static void WriteLogin(string account, string token)
		{
			try
			{
				File.WriteAllLines(LoginFile, new[] { account, token });
			}
			catch (Exception e)
			{
				Log("the sign-in could not be remembered: " + e.Message);
			}
		}

		private static void DeleteLogin()
		{
			try
			{
				File.Delete(LoginFile);
			}
			catch (Exception)
			{
			}
		}

		// ---- what the downloads need ----

		public static bool Online => State == SessionState.Online && connected;

		private static void RequireOnline()
		{
			if (!Online)
			{
				throw new InvalidOperationException(Texts.Say("没有登录 Steam。", "Not signed in to Steam."));
			}
		}

		/// <summary>The details of workshop items (with their required items or, for a collection, its items).</summary>
		public static async Task<List<PublishedFileDetails>> Details(IReadOnlyList<ulong> ids)
		{
			RequireOnline();
			var all = new List<PublishedFileDetails>();
			for (int at = 0; at < ids.Count; at += 50)
			{
				var request = new CPublishedFile_GetDetails_Request { includechildren = true, appid = App };
				request.publishedfileids.AddRange(ids.Skip(at).Take(50));
				var response = await publishedFiles.GetDetails(request);
				if (response.Result != EResult.OK)
				{
					throw new Exception(Texts.Say("读取 mod 信息失败：", "Reading the items failed: ") + response.Result);
				}
				all.AddRange(response.Body.publishedfiledetails);
			}
			return all;
		}

		/// <summary>The account's subscriptions for RimWorld.</summary>
		public static async Task<List<PublishedFileDetails>> Subscriptions()
		{
			RequireOnline();
			var all = new List<PublishedFileDetails>();
			for (uint page = 1; page <= 50; page++)
			{
				var request = new CPublishedFile_GetUserFiles_Request
				{
					steamid = client.SteamID.ConvertToUInt64(),
					appid = App,
					type = "mysubscriptions",
					page = page,
					numperpage = 100,
					return_children = true,
				};
				var response = await publishedFiles.GetUserFiles(request);
				if (response.Result != EResult.OK)
				{
					throw new Exception(Texts.Say("读取订阅失败：", "Reading the subscriptions failed: ") + response.Result);
				}
				List<PublishedFileDetails> got = response.Body.publishedfiledetails;
				all.AddRange(got);
				if (got.Count < 100 || all.Count >= response.Body.total)
				{
					break;
				}
			}
			return all;
		}

		public static async Task<byte[]> DepotKey()
		{
			RequireOnline();
			if (depotKey == null)
			{
				SteamApps.DepotKeyCallback key = await apps.GetDepotDecryptionKey(App, App);
				if (key.Result != EResult.OK)
				{
					throw new Exception(Texts.Say("这个账号没有下载 RimWorld 内容的权限（" + key.Result + "）。", "This account may not download RimWorld's content (" + key.Result + ")."));
				}
				depotKey = key.DepotKey;
			}
			return depotKey;
		}

		/// <summary>Steam's content servers for this place, the least loaded first.</summary>
		public static async Task<List<Server>> Servers()
		{
			RequireOnline();
			if (servers == null || servers.Count == 0)
			{
				// Through the connection to Steam (the web API behind ContentServerDirectoryService is slow from some places).
				IReadOnlyCollection<Server> found;
				try
				{
					found = await content.GetServersForSteamPipe(cellId);
				}
				catch (Exception e)
				{
					Log("content servers through the connection failed (" + e.Message + "), asking the web API");
					found = await ContentServerDirectoryService.LoadAsync(client.Configuration, cellId, CancellationToken.None);
				}
				List<Server> usable = found.Where(s => (s.Type == "SteamCache" || s.Type == "CDN") && (s.AllowedAppIds == null || s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(App))).OrderBy(s => s.WeightedLoad).ToList();
				servers = usable.Count > 0 ? usable : found.ToList();
				Log(servers.Count + " content servers: " + string.Join(", ", servers.Take(4).Select(s => s.Host)));
			}
			return servers;
		}

		public static Task<ulong> RequestCode(ulong manifest)
		{
			RequireOnline();
			return content.GetManifestRequestCode(App, App, manifest, "public");
		}

		public static Client Cdn => cdn;
	}
}
