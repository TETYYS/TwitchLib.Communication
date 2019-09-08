using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Communication.Events;
using TwitchLib.Communication.Interfaces;
using TwitchLib.Communication.Models;

namespace TwitchLib.Communication.Clients
{
	public class WebSocketClient
	{
		const string WSS_SERVER = "wss://pubsub-edge.twitch.tv:443";
		public TimeSpan DefaultKeepAliveInterval { get; set; }

		public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

		public int SendQueueLength { get; set; }

		public int WhisperQueueLength { get; set; }

		public bool IsConnected => WSConnection?.State == WebSocketState.Open;

		public IClientOptions Options { get; set; }

		public event EventHandler<OnConnectedEventArgs> OnConnected;
		public event EventHandler<OnDataEventArgs> OnData;
		public event EventHandler<OnDisconnectedEventArgs> OnDisconnected;
		public event EventHandler<OnErrorEventArgs> OnError;
		public event EventHandler<OnFatalErrorEventArgs> OnFatality;
		public event EventHandler<OnMessageEventArgs> OnMessage;
		public event EventHandler<OnMessageThrottledEventArgs> OnMessageThrottled;
		public event EventHandler<OnWhisperThrottledEventArgs> OnWhisperThrottled;
		public event EventHandler<OnSendFailedEventArgs> OnSendFailed;
		public event EventHandler<OnStateChangedEventArgs> OnStateChanged;
		public event EventHandler<OnReconnectedEventArgs> OnReconnected;

		private ClientWebSocket WSConnection = new ClientWebSocket();
		private Task QueueConsume;
		private Task StreamReader;

		public WebSocketClient(IClientOptions options = null)
		{
			Options = options ?? new ClientOptions();

			if (Options.ClientType == TwitchLib.Communication.Enums.ClientType.Chat)
				throw new Exception("Use TCP");
		}

		public void Close()
		{
			if (IsConnected) {
				try { WSConnection.Abort(); } catch { }
			}

			OnDisconnected?.Invoke(this, new OnDisconnectedEventArgs());
		}

		private int ReconnectAttempts = 0;

		public async Task<bool> OpenImpl(bool TryReconnect)
		{
			if (IsConnected)
				return true;

			WSConnection = new ClientWebSocket();
			var connectCancel = new CancellationTokenSource();
			var connTask = WSConnection.ConnectAsync(new Uri(WSS_SERVER), connectCancel.Token);

			await Task.WhenAny(connTask, Task.Delay(ConnectionTimeout));

			if (!connTask.IsCompleted) {
				connectCancel.Cancel();

				if (Options.ReconnectionPolicy != null && TryReconnect) {
					ReconnectAttempts = 0;
					while (!Options.ReconnectionPolicy.AreAttemptsComplete()) {
						await Task.Delay(Options.ReconnectionPolicy.GetReconnectInterval());
						ReconnectAttempts++;
						Options.ReconnectionPolicy.SetAttemptsMade(ReconnectAttempts);
						if (await OpenImpl(false))
							break;
					}
				}
				return false;
			}

			StreamReader = Task.Run(async () => await Reader().ConfigureAwait(false));

			Options.ReconnectionPolicy.Reset();
			ReconnectAttempts = 0;
			OnConnected?.Invoke(this, new OnConnectedEventArgs());

			return true;
		}

		public Task<bool> Open()
		{
			return OpenImpl(true);
		}
		

		public async Task Reconnect()
		{
			Close();
			await Open();
			OnReconnected?.Invoke(this, new OnReconnectedEventArgs());
		}

		public async Task<bool> Send(string message)
		{
			if (!IsConnected)
				return false;

			await WSConnection.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, default);

			return true;
		}

		private async Task Reader()
		{
			var buffer = new ArraySegment<byte>(new byte[1024]);
			
			StringBuilder sb = new StringBuilder();

			while (IsConnected) {
				try {
					var res = await WSConnection.ReceiveAsync(buffer, default);
					
					if (res.MessageType == WebSocketMessageType.Close) {
						Close();
						break;
					}

					if (res.Count == 0) {
						Close();
						break;
					}

					switch (res.MessageType) {
						case WebSocketMessageType.Close:
							Close();
							break;
						case WebSocketMessageType.Text when !res.EndOfMessage:
							sb.Append(Encoding.UTF8.GetString(buffer.Array, 0, res.Count).TrimEnd('\0'));
							continue;
						case WebSocketMessageType.Text:
							sb.Append(Encoding.UTF8.GetString(buffer.Array, 0, res.Count).TrimEnd('\0'));
							OnMessage?.Invoke(this, new OnMessageEventArgs() { Message = sb.ToString() });
							break;
						case WebSocketMessageType.Binary:
							break;
						default:
							throw new ArgumentOutOfRangeException();
					}

					sb.Clear();
				} catch (IOException) {
					Close();
				} catch (Exception ex) {
					OnError?.Invoke(this, new OnErrorEventArgs() {
						Exception = ex
					});
					break;
				}
			}
		}
	}
}
